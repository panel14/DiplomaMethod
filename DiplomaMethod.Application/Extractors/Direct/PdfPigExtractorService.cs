using DiplomaMethod.Application.Extractors.Analyzers;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Services;
using System.Diagnostics;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DiplomaMethod.Application.Extractors.Direct;

public class PdfPigExtractorService(ITextValidator validator) : IExtractor
{
    private readonly ITextValidator _validator = validator;

    public async Task<IReadOnlyList<TextBlock>> ReadAsync(
        LayoutImage image,
        IReadOnlyList<DetectionResult> detection)
    {
        return await Task.Run(() =>
        {
            var words = ExtractAllWords(image.ImageStream, image.Page.PageNumber);

            if (!_validator.ValidateStrongText(words[0..Math.Min(50, words.Count)]))
            {
                return [..detection.Select(d => new TextBlock {Box = d.BoundingBox, Label = d.Label, Confidence = 0})];
            }

            var resultBlocks = new List<TextBlock>();

            foreach (var det in detection)
            {
                var areaWords = ExtractWordsFromArea(det, words);
                var wordClusters = DocstrumPdfAnalyzer.ClusterWords(areaWords);
                foreach (var cluster in wordClusters)
                {
                    var textBlock = BuildLines(cluster, det.Label);
                    var splittedBlocks = SplitByLineIndent(textBlock, det.Label);

                    foreach (var sb in splittedBlocks)
                    {
                        var metrics = _validator.EvaluateBlock(sb);
                        sb.Confidence = metrics.Length > 0
                            ? Math.Clamp(1.0 - metrics.Average(), 0.0, 1.0)
                            : 1.0;
                    }
                    resultBlocks.AddRange(splittedBlocks);
                }
            }

            return resultBlocks;
        });
    }

    public static List<Word> ExtractAllWords(Stream docStream, int pageNumber)
    {
        try
        {
            docStream.Seek(0, SeekOrigin.Begin);
            using var document = PdfDocument.Open(docStream, new ParsingOptions { UseLenientParsing = true});
            return [.. document.GetPage(pageNumber).GetWords()];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during PDF word extraction: {ex}");
            throw;
        }
    }

    public static List<Word> ExtractWordsFromArea(DetectionResult detection, List<Word> words)
    {
        var box = detection.BoundingBox;
        double minX = box.X;
        double maxX = box.X + box.Width;
        double minY = box.Y;
        double maxY = box.Y + box.Height;

        return [.. words.Where(w =>
            w.BoundingBox.Left >= minX &&
            w.BoundingBox.Right <= maxX &&
            w.BoundingBox.Bottom >= minY &&
            w.BoundingBox.Top <= maxY)];
    }

    public static List<TextBlock> SplitByLineIndent(TextBlock block, string label)
        => TextBlockAnalyzer.SplitByLineIndent(block, label);

    public static TextBlock BuildLines(IEnumerable<Word> words, string label)
    {
        var textBlock = new TextBlock { Label = label, };
        if (words == null || !words.Any())
        {
            return textBlock;
        }

        var sortedWords = words.ToList();
        sortedWords.Sort((a, b) => b.BoundingBox.Bottom.CompareTo(a.BoundingBox.Bottom));

        var textLines = new List<TextLine>();

        int startIndex = 0;
        double currentLineAvgBottom = sortedWords[0].BoundingBox.Bottom;
        double tolerance = sortedWords[0].BoundingBox.Height * 0.5;

        for (int i = 1; i <= sortedWords.Count; i++)
        {
            if (i == sortedWords.Count || Math.Abs(sortedWords[i].BoundingBox.Bottom - currentLineAvgBottom) > tolerance)
            {
                // Формируем линию из текущего спана
                int count = i - startIndex;
                var lineSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sortedWords).Slice(startIndex, count);

                // Сортировка слов в строке слева-направо (X) in-place в спане
                lineSpan.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                var sb = new System.Text.StringBuilder(count * 5); // Примерный начальный размер

                for (int j = 0; j < lineSpan.Length; j++)
                {
                    var w = lineSpan[j];
                    if (w.BoundingBox.Left < minX) minX = w.BoundingBox.Left;
                    if (w.BoundingBox.Bottom < minY) minY = w.BoundingBox.Bottom;
                    if (w.BoundingBox.Right > maxX) maxX = w.BoundingBox.Right;
                    if (w.BoundingBox.Top > maxY) maxY = w.BoundingBox.Top;

                    if (j > 0) sb.Append(' ');
                    sb.Append(w.Text);
                }

                textLines.Add(new TextLine
                {
                    Text = sb.ToString(),
                    Box = new BoundingBox(minX, minY, maxX - minX, maxY - minY)
                });

                if (i < sortedWords.Count)
                {
                    startIndex = i;
                    currentLineAvgBottom = sortedWords[i].BoundingBox.Bottom;
                    tolerance = sortedWords[i].BoundingBox.Height * 0.5;
                }
            }
            else
            {
                // Пересчитываем среднюю позицию (быстрый скользящий средний)
                int currentLen = i - startIndex + 1;
                currentLineAvgBottom += (sortedWords[i].BoundingBox.Bottom - currentLineAvgBottom) / currentLen;
            }
        }

        textBlock.Lines = textLines;

        if (textLines.Count > 0)
        {
            double overallMinX = double.MaxValue, overallMinY = double.MaxValue;
            double overallMaxX = double.MinValue, overallMaxY = double.MinValue;

            foreach (var l in textLines)
            {
                if (l.Box.X < overallMinX) overallMinX = l.Box.X;
                if (l.Box.Y < overallMinY) overallMinY = l.Box.Y;
                if (l.Box.X + l.Box.Width > overallMaxX) overallMaxX = l.Box.X + l.Box.Width;
                if (l.Box.Y + l.Box.Height > overallMaxY) overallMaxY = l.Box.Y + l.Box.Height;
            }

            textBlock.Box = new BoundingBox(overallMinX, overallMinY, overallMaxX - overallMinX, overallMaxY - overallMinY);
        }

        return textBlock;
    }
}