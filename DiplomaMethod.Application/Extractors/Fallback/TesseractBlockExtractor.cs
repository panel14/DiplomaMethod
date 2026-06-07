using DiplomaMethod.Application.Extractors.Ocr.Heuristics;
using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Services;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Fallback;

public class TesseractBlockExtractor(
    TesseractOcrRecognizer recognizer,
    IReadOnlyList<IStringHeuristic>? lineHeuristics = null) : IExtractor
{
    private readonly IReadOnlyList<IStringHeuristic> _lineHeuristics = lineHeuristics ?? [];

    public async Task<IReadOnlyList<TextBlock>> ReadAsync(LayoutImage image, IReadOnlyList<DetectionResult> detection)
    {
        var source = image.CachedImage;
        if (source == null) return [];

        var results = new List<TextBlock>();

        foreach (var det in detection)
        {
            using var blockImage = CropImage(source, det.BoundingBox);
            if (blockImage == null) continue;

            var text = await recognizer.RecognizeLineAsync(blockImage);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var lines = new List<TextLine>();
            double totalConf = 0.0;

            foreach (var lineText in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (lineText.Length == 0) continue;
                totalConf += ComputeConfidence(lineText);
                lines.Add(new TextLine { Text = lineText, Box = det.BoundingBox });
            }

            if (lines.Count > 0)
                results.Add(new TextBlock
                {
                    Lines = lines,
                    Box = det.BoundingBox,
                    Label = string.IsNullOrEmpty(det.Label) ? "Text" : det.Label,
                    Confidence = totalConf / lines.Count
                });
        }

        return results;
    }

    private double ComputeConfidence(string text)
    {
        if (_lineHeuristics.Count == 0) return 1.0;
        double penalty = _lineHeuristics.Average(h => h.Evaluate(text));
        return Math.Clamp(1.0 - penalty, 0.0, 1.0);
    }

    private static SKImage? CropImage(SKImage source, BoundingBox bbox)
    {
        var x = Math.Max(0, (int)bbox.X);
        var y = Math.Max(0, (int)bbox.Y);
        var w = Math.Min((int)bbox.Width, source.Width - x);
        var h = Math.Min((int)bbox.Height, source.Height - y);
        if (w <= 0 || h <= 0) return null;
        return source.Subset(new SKRectI(x, y, x + w, y + h));
    }
}
