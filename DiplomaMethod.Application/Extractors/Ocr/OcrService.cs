using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using DiplomaMethod.Application.Diagnostics;
using DiplomaMethod.Application.Extractors.Analyzers;
using DiplomaMethod.Application.Extractors.Ocr.Heuristics;
using DiplomaMethod.Application.Extractors.Ocr.Postprocessors;
using DiplomaMethod.Application.Extractors.Ocr.Preprosessors;
using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Line;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Window;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using DiplomaMethod.Core.Services;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr;

public class OcrService(
    OcrOptions options,
    PaddleOcrRecognizerV5Options paddleOptions,
    ILineSplitter? lineSplitter = null,
    IWordSplitter? wordSplitter = null,
    IReadOnlyList<IStringHeuristic>? lineHeuristics = null,
    ILinePostprocessor? linePostprocessor = null,
    ILineWindowSplitter? windowSplitter = null)
    : IExtractor, IDisposable
{
    private readonly OcrOptions _options = options;
    private readonly PaddleOcrRecognizer_V5 _paddle = new(paddleOptions);
    private readonly ILineSplitter? _lineSplitter = lineSplitter;
    private readonly IWordSplitter? _wordSplitter = wordSplitter;
    private readonly ILinePostprocessor? _linePostprocessor = linePostprocessor;
    private readonly ILineWindowSplitter? _windowSplitter = windowSplitter;
    private readonly IReadOnlyList<IStringHeuristic> _lineHeuristics = lineHeuristics ?? [];
    private bool _disposed;

    public async Task<IReadOnlyList<TextBlock>> ReadAsync(LayoutImage image, IReadOnlyList<DetectionResult> detection)
    {
        return await Task.Run(async () =>
        {
            var sourceImage = GetSourceImage(image, out bool ownSource);
            if (sourceImage == null) return [];
            try
            {
                return await ProcessImageAsync(sourceImage, detection);
            }
            finally
            {
                if (ownSource) sourceImage.Dispose();
            }
        });
    }

    // Cap on images per Paddle inference call (see RecognizeBucketedAsync). Bounds peak activation
    // memory per batch while keeping batches large enough to amortize call overhead. Configurable.
    private int MaxInferenceBatch => Math.Max(1, _options.MaxInferenceBatch);

    private async Task<IReadOnlyList<TextBlock>> ProcessImageAsync(
        SKImage sourceImage, IReadOnlyList<DetectionResult> detection)
    {
        // Window mode batches every window of the whole page together (see ProcessImageWindowedAsync);
        // the other modes keep the per-component recognition path.
        if (_options.WordSegmentation == WordSegmentationMode.Window && _windowSplitter != null)
            return await ProcessImageWindowedAsync(sourceImage, detection);

        return await ProcessImagePerComponentAsync(sourceImage, detection);
    }

    // ── Per-component path (modes None / WordLevelOcr / Proportional) ────────────
    private async Task<IReadOnlyList<TextBlock>> ProcessImagePerComponentAsync(
        SKImage sourceImage, IReadOnlyList<DetectionResult> detection)
    {
        var results = new List<TextBlock>();

        foreach (var det in detection)
        {
            using var blockImage = CropImage(sourceImage, det.BoundingBox);
            if (blockImage == null) continue;

            string blockLabel = string.IsNullOrEmpty(det.Label) ? "Text" : det.Label;

            foreach (var component in GetComponents(blockImage, det.Label, _options.UseDocstrumClustering))
            {
                using var componentImage = CropImage(blockImage, component);
                if (componentImage == null) continue;

                SKImage? preprocessed = OcrPreprocessor.Process(componentImage, _options.Preprocess);
                SKImage workImage = preprocessed ?? componentImage;
                try
                {
                    var lineRegions = SplitLines(workImage);
                    if (lineRegions.Count == 0) continue;

                    var lineImages = lineRegions.Select(r => workImage.Subset(r)).ToList();
                    UpscaleLineImages(lineImages, _options.Preprocess);
                    try
                    {
                        var lineResults = await RecognizeLinesAsync(lineImages);
                        AssembleComponent(component, det.BoundingBox.X, det.BoundingBox.Y,
                            lineRegions, lineResults, blockLabel, results);
                    }
                    finally
                    {
                        foreach (var img in lineImages) img?.Dispose();
                    }
                }
                finally
                {
                    preprocessed?.Dispose();
                }
            }
        }
        return results;
    }

    // ── Page-level windowed path (mode Window) ──────────────────────────────────
    // Gathers the windows of every line on the page into one list, recognizes them all in
    // width-bucketed, size-capped batches (far fewer and better-utilized Paddle calls than the
    // per-component path), then rejoins each line's window texts with a space.
    private async Task<IReadOnlyList<TextBlock>> ProcessImageWindowedAsync(
        SKImage sourceImage, IReadOnlyList<DetectionResult> detection)
    {
        var results    = new List<TextBlock>();
        var allWindows = new List<SKImage>();
        var comps      = new List<PendingComponent>();

        // ── Gather: split every line into windows (independent copies) ───────────
        foreach (var det in detection)
        {
            using var blockImage = CropImage(sourceImage, det.BoundingBox);
            if (blockImage == null) continue;

            string blockLabel = string.IsNullOrEmpty(det.Label) ? "Text" : det.Label;

            foreach (var component in GetComponents(blockImage, det.Label, _options.UseDocstrumClustering))
            {
                using var componentImage = CropImage(blockImage, component);
                if (componentImage == null) continue;

                SKImage? preprocessed = OcrPreprocessor.Process(componentImage, _options.Preprocess);
                SKImage workImage = preprocessed ?? componentImage;
                try
                {
                    var lineRegions = SplitLines(workImage);
                    if (lineRegions.Count == 0) continue;

                    var pending = new PendingComponent(
                        component, det.BoundingBox.X, det.BoundingBox.Y, blockLabel, lineRegions);
                    foreach (var region in lineRegions)
                    {
                        using var lineImg = workImage.Subset(region);
                        long tWin = Stopwatch.GetTimestamp();
                        var windows = _windowSplitter!.Split(lineImg);
                        if (OcrProfiler.Enabled) OcrProfiler.AddWindowSplit(Stopwatch.GetTimestamp() - tWin);

                        pending.LineWindowStart.Add(allWindows.Count);
                        pending.LineWindowCount.Add(windows.Count);
                        allWindows.AddRange(windows);
                    }
                    comps.Add(pending);
                }
                finally
                {
                    preprocessed?.Dispose();
                }
            }
        }

        if (allWindows.Count == 0) return results;

        // ── Recognize all windows page-wide, then assemble ──────────────────────
        try
        {
            var windowResults = await RecognizeBucketedAsync(allWindows);

            foreach (var pc in comps)
            {
                var lineResults = new (string Text, float Score)[pc.LineRegions.Count];
                for (int li = 0; li < pc.LineRegions.Count; li++)
                    lineResults[li] = JoinWindows(windowResults, pc.LineWindowStart[li], pc.LineWindowCount[li]);

                AssembleComponent(pc.Box, pc.DetOffsetX, pc.DetOffsetY,
                    pc.LineRegions, lineResults, pc.BlockLabel, results);
            }
        }
        finally
        {
            foreach (var img in allWindows) img?.Dispose();
        }

        return results;
    }

    // Layout components of a block: the whole box for atomic labels (or when Docstrum clustering is
    // disabled), Docstrum clusters otherwise, sorted top-to-bottom then left-to-right.
    private static List<BoundingBox> GetComponents(SKImage blockImage, string? label, bool useDocstrum)
    {
        List<BoundingBox> components = (!useDocstrum || IsAtomicLabel(label))
            ? [new BoundingBox(0, 0, blockImage.Width, blockImage.Height)]
            : DocstrumImageAnalyzer.ClusterComponents(blockImage);

        components.Sort(static (a, b) =>
        {
            int c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.X.CompareTo(b.X);
        });
        return components;
    }

    // Splits a component image into line regions (LSL + optional postprocess), or the whole image
    // when no line splitter is configured.
    private List<SKRectI> SplitLines(SKImage workImage)
    {
        if (_lineSplitter == null)
            return [new SKRectI(0, 0, workImage.Width, workImage.Height)];

        long tLsl = Stopwatch.GetTimestamp();
        var lineRegions = _lineSplitter.Split(workImage);
        if (OcrProfiler.Enabled) OcrProfiler.AddLsl(Stopwatch.GetTimestamp() - tLsl);

        return _linePostprocessor != null ? _linePostprocessor.Process(lineRegions) : lineRegions;
    }

    // Joins the recognized texts of a line's windows with a space (every window boundary is an
    // inter-word gap), averaging the per-window scores.
    private static (string Text, float Score) JoinWindows(
        IReadOnlyList<(string Text, float Score)> windowResults, int start, int count)
    {
        var    sb    = new StringBuilder();
        double total = 0.0;
        int    good  = 0;

        for (int w = start; w < start + count; w++)
        {
            var (wText, wScore) = windowResults[w];
            if (!string.IsNullOrWhiteSpace(wText))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(wText.Trim());
                total += wScore;
                good++;
            }
        }
        return (sb.ToString(), good > 0 ? (float)(total / good) : 0f);
    }

    // Builds TextLines from recognized line results and adds the resulting paragraph blocks.
    // offsetX/offsetY translate component-local coordinates into source-image (page) space — the
    // component box is relative to the cropped detection region, so the detection's top-left is added
    // back so the pipeline can match the returned blocks against the page-space detections.
    private void AssembleComponent(
        BoundingBox component, double offsetX, double offsetY, IReadOnlyList<SKRectI> lineRegions,
        IReadOnlyList<(string Text, float Score)> lineResults, string blockLabel, List<TextBlock> results)
    {
        var    lines               = new List<TextLine>();
        double totalLineConfidence = 0.0;

        for (int i = 0; i < lineRegions.Count; i++)
        {
            var (text, score) = lineResults[i];
            if (string.IsNullOrWhiteSpace(text)) continue;

            totalLineConfidence += ComputeLineConfidence(text, score);
            lines.Add(new TextLine
            {
                Text = text,
                Box  = new BoundingBox(
                    component.X + offsetX + lineRegions[i].Left,
                    component.Y + offsetY + lineRegions[i].Top,
                    lineRegions[i].Width,
                    lineRegions[i].Height)
            });
        }

        if (lines.Count == 0) return;

        double componentConf = totalLineConfidence / lines.Count;
        var pageBox    = new BoundingBox(
            component.X + offsetX, component.Y + offsetY, component.Width, component.Height);
        var rawBlock   = new TextBlock { Lines = lines, Box = pageBox, Label = blockLabel };
        var paragraphs = TextBlockAnalyzer.SplitByLineIndent(rawBlock, blockLabel);

        foreach (var para in paragraphs)
        {
            para.Confidence = componentConf;
            results.Add(para);
        }
    }

    // Recognizes all images in width-bucketed, size-capped batches: images are sorted by width so
    // each batch is width-homogeneous (minimal white padding), and split into chunks of at most
    // MaxInferenceBatch so a whole page's windows don't form one oversized tensor. Results are
    // returned in the original input order.
    private async Task<(string Text, float Score)[]> RecognizeBucketedAsync(IReadOnlyList<SKImage> images)
    {
        int n = images.Count;
        var results = new (string Text, float Score)[n];

        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => images[a].Width.CompareTo(images[b].Width));

        for (int start = 0; start < n; start += MaxInferenceBatch)
        {
            int len = Math.Min(MaxInferenceBatch, n - start);
            var batch = new List<SKImage>(len);
            for (int k = 0; k < len; k++) batch.Add(images[order[start + k]]);

            var batchResults = await _paddle.RecognizeBatchAsync(batch);
            for (int k = 0; k < len; k++) results[order[start + k]] = batchResults[k];
        }
        return results;
    }

    // Gathered lines of one component, awaiting page-level window recognition.
    private sealed class PendingComponent(
        BoundingBox box, double detOffsetX, double detOffsetY, string blockLabel, List<SKRectI> lineRegions)
    {
        public BoundingBox Box { get; } = box;
        public double DetOffsetX { get; } = detOffsetX;
        public double DetOffsetY { get; } = detOffsetY;
        public string BlockLabel { get; } = blockLabel;
        public List<SKRectI> LineRegions { get; } = lineRegions;
        public List<int> LineWindowStart { get; } = [];
        public List<int> LineWindowCount { get; } = [];
    }

    private async Task<IReadOnlyList<(string Text, float Score)>> RecognizeLinesAsync(
        List<SKImage> lineImages)
    {
        return _options.WordSegmentation switch
        {
            WordSegmentationMode.WordLevelOcr when _wordSplitter != null
                => await RecognizeByWordLevelOcrAsync(lineImages),
            WordSegmentationMode.Proportional when _wordSplitter != null
                => await RecognizeByProportionalSplitAsync(lineImages),
            _   => await _paddle.RecognizeBatchAsync(lineImages),
        };
    }

    private async Task<IReadOnlyList<(string Text, float Score)>> RecognizeByWordLevelOcrAsync(
        List<SKImage> lineImages)
    {
        var wordBatch  = new List<SKImage>(lineImages.Count * 5);
        var lineStarts = new int[lineImages.Count + 1];

        for (int i = 0; i < lineImages.Count; i++)
        {
            lineStarts[i] = wordBatch.Count;
            var wordRects = _wordSplitter!.Split(lineImages[i]);

            if (wordRects.Count > 0)
                foreach (var r in wordRects)
                    wordBatch.Add(lineImages[i].Subset(r));
            else
                wordBatch.Add(lineImages[i].Subset(
                    new SKRectI(0, 0, lineImages[i].Width, lineImages[i].Height)));
        }
        lineStarts[lineImages.Count] = wordBatch.Count;

        try
        {
            var wordResults = await _paddle.RecognizeBatchAsync(wordBatch);
            var lineResults = new (string Text, float Score)[lineImages.Count];

            for (int i = 0; i < lineImages.Count; i++)
            {
                int start = lineStarts[i];
                int end   = lineStarts[i + 1];

                var sb    = new StringBuilder(end - start);
                double total = 0.0;
                int    good  = 0;

                for (int w = start; w < end; w++)
                {
                    var (wText, wScore) = wordResults[w];
                    if (!string.IsNullOrWhiteSpace(wText))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(wText);
                        total += wScore;
                        good++;
                    }
                }

                lineResults[i] = (sb.ToString(), good > 0 ? (float)(total / good) : 0f);
            }

            return lineResults;
        }
        finally
        {
            foreach (var img in wordBatch) img?.Dispose();
        }
    }

    private async Task<IReadOnlyList<(string Text, float Score)>> RecognizeByProportionalSplitAsync(
        List<SKImage> lineImages)
    {
        var lineResults = await _paddle.RecognizeBatchAsync(lineImages);
        var output      = new (string Text, float Score)[lineImages.Count];

        for (int i = 0; i < lineImages.Count; i++)
        {
            var (text, score) = lineResults[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                output[i] = (text, score);
                continue;
            }

            var wordSpans = _wordSplitter!.Split(lineImages[i]);
            output[i] = (DistributeCharacters(text, wordSpans), score);
        }

        return output;
    }

    // Distributes the characters of a flat OCR string into words according to pixel-width ratios.
    // Uses the largest-remainder (Hamilton/Hare) method: each span gets floor(quota), then the
    // remaining characters go to spans with the largest fractional remainders. Each span gets ≥ 1 char.
    // This avoids carry-based drift that can cause ±1 errors when adjacent spans have different
    // characters-per-pixel densities.
    internal static string DistributeCharacters(string text, List<SKRectI> wordSpans)
    {
        if (wordSpans.Count == 0) return text;
        if (wordSpans.Count == 1) return text;

        int n = text.Length;
        if (n == 0) return text;

        double totalWidth = 0;
        foreach (var s in wordSpans) totalWidth += s.Width;
        if (totalWidth <= 0) return text;

        int k          = wordSpans.Count;
        var counts     = new int[k];
        var remainders = new double[k];
        int assigned   = 0;

        for (int w = 0; w < k; w++)
        {
            double quota = wordSpans[w].Width / totalWidth * n;
            counts[w]     = Math.Max(1, (int)quota); // floor, minimum 1
            remainders[w] = quota - counts[w];
            assigned     += counts[w];
        }

        // Give extra chars to spans with the largest remainders
        for (int extra = n - assigned; extra > 0; extra--)
        {
            int best = 0;
            for (int w = 1; w < k; w++)
                if (remainders[w] > remainders[best]) best = w;
            counts[best]++;
            remainders[best]--;
        }

        // Rarely: Math.Max(1, floor) on very narrow spans may cause over-allocation — trim from
        // spans with the most negative remainder (furthest from their quota), never below 1
        for (int over = assigned - n; over > 0; over--)
        {
            int worst = -1;
            for (int w = 0; w < k; w++)
                if (counts[w] > 1 && (worst < 0 || remainders[w] < remainders[worst])) worst = w;
            if (worst < 0) break;
            counts[worst]--;
            remainders[worst]++;
        }

        var sb      = new StringBuilder(n + k - 1);
        int charPos = 0;
        for (int w = 0; w < k; w++)
        {
            int take = Math.Min(counts[w], n - charPos);
            if (take <= 0) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(text, charPos, take);
            charPos += take;
        }

        if (charPos < n)
            sb.Append(text, charPos, n - charPos);

        return sb.ToString();
    }

    internal static void UpscaleLineImages(List<SKImage> lineImages, OcrPreprocessOptions opts)
    {
        if (!opts.Enabled || opts.LineUpscaleMinHeight <= 0) return;

        for (int i = 0; i < lineImages.Count; i++)
        {
            var img = lineImages[i];
            if (img == null || img.Height >= opts.LineUpscaleMinHeight) continue;

            int newH = opts.LineUpscaleTargetHeight;
            int newW = Math.Max(1, (int)Math.Ceiling(img.Width * (double)newH / img.Height));

            using var surface = SKSurface.Create(new SKImageInfo(newW, newH));
            if (surface == null) continue;

            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawImage(img, new SKRect(0, 0, newW, newH));

            img.Dispose();
            lineImages[i] = surface.Snapshot();
        }
    }

    private static SKImage? GetSourceImage(LayoutImage image, out bool owned)
    {
        if (image.CachedImage != null)
        {
            owned = false;
            return image.CachedImage;
        }
        image.ImageStream.Seek(0, SeekOrigin.Begin);
        owned = true;
        return SKImage.FromEncodedData(image.ImageStream);
    }

    private static SKImage? CropImage(SKImage source, BoundingBox bbox)
    {
        var x = Math.Max(0, (int)bbox.X);
        var y = Math.Max(0, (int)bbox.Y);
        var width = Math.Min((int)bbox.Width, source.Width - x);
        var height = Math.Min((int)bbox.Height, source.Height - y);
        if (width <= 0 || height <= 0) return null;
        return source.Subset(new SKRectI(x, y, x + width, y + height));
    }

    private double ComputeLineConfidence(string text, float baseScore)
    {
        if (_lineHeuristics.Count == 0)
            return Math.Clamp(baseScore, 0.0, 1.0);

        double penalty = _lineHeuristics.Average(h => h.Evaluate(text));
        return Math.Clamp(baseScore * (1.0 - penalty), 0.0, 1.0);
    }

    // Labels for which the classifier bounding box is already the final region —
    // no need to run Docstrum to find sub-structure.
    private static readonly FrozenSet<string> AtomicLabels = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Title", "Section Header", "Caption", "Page Header", "Page Footer"
    );

    internal static bool IsAtomicLabel(string? label)
        => !string.IsNullOrEmpty(label) && AtomicLabels.Contains(label);

    public void Dispose()
    {
        if (!_disposed)
        {
            _paddle.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
