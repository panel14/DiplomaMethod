using DiplomaMethod.Application.Classifiers;
using DiplomaMethod.Application.DocumentReader;
using DiplomaMethod.Application.Extractors.Analyzers;
using DiplomaMethod.Application.Extractors.Ocr;
using DiplomaMethod.Application.Extractors.Ocr.Preprosessors;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Line;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.UtilsTests;
using SkiaSharp;

namespace DiplomaMethod.UnitTests.IntegrationTests;

/// <summary>
/// Diagnostic test that saves intermediate pipeline images for visual inspection:
///   det{N}_{Label}_docstrum.png        — detection block with Docstrum component boxes
///   det{N}_{Label}_comp{M}_lines_pre.png  — line strips before UpscaleLineImages
///   det{N}_{Label}_comp{M}_lines_post.png — line strips after  UpscaleLineImages
/// </summary>
[TestFixture]
[Category("Integration")]
public class OcrPipelineDiagnosticsTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "Integration:YoloClassifierOcrService";

    private string ModelPath => GetConfigValue("ModelPath");

    private string ResultsBase =>
        Path.Combine(Path.GetDirectoryName(GetConfigValue("ResultsPath"))!, "OcrPipelineDiagnostics");

    private const int MaxComponents = 4;
    private const int MaxLines      = 8;

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    //[TestCase("PdfPath")]
    [TestCase("PdfScanPath1")]
    [TestCase("PdfScanPath2")]
    [TestCase("ImagePath")]
    //[TestCase("PdfPathRuDirect")]
    public async Task SavePipelineVisualizations(string documentKey)
    {
        SkipIfModelMissing();
        var docPath = GetConfigValue(documentKey);
        Assume.That(File.Exists(docPath), Is.True, $"File not found: '{docPath}'.");

        var opts    = ReadOcrOptions();
        var docName = Path.GetFileNameWithoutExtension(docPath);
        var outDir  = Path.Combine(ResultsBase, docName);
        Directory.CreateDirectory(outDir);

        using var page = await ReadFirstPageAsync(docPath);
        var sourceImage = page.CachedImage;
        Assume.That(sourceImage, Is.Not.Null, "Page has no CachedImage.");

        Console.WriteLine($"[{docName}]  {sourceImage.Width}×{sourceImage.Height}px");
        Console.WriteLine($"Output: {outDir}");

        using var classifier = new YoloLayoutClassifier(ModelPath);
        var detections = (await classifier.ClassifyAsync(page)).ToList();
        Console.WriteLine($"Detections: {detections.Count}");

        int savedFiles = 0;

        for (int di = 0; di < detections.Count; di++)
        {
            var det = detections[di];
            using var blockImage = CropImage(sourceImage, det.BoundingBox);
            if (blockImage == null) continue;

            var safeLabel = det.Label.Replace(" ", "_");
            var prefix    = $"det{di:D2}_{safeLabel}";

            // ── Step 1: Docstrum visualization ───────────────────────────────
            var components = OcrService.IsAtomicLabel(det.Label)
                ? [new BoundingBox(0, 0, blockImage.Width, blockImage.Height)]
                : DocstrumImageAnalyzer.ClusterComponents(blockImage);

            string docFile = ImageTestUtils.SaveWithBoundingBoxes(
                blockImage, components, outDir, $"{prefix}_docstrum.png");
            Console.WriteLine($"  [{di:D2}] {det.Label,-16} {blockImage.Width}×{blockImage.Height}px  " +
                              $"{components.Count} comp(s)  → {Path.GetFileName(docFile)}");
            savedFiles++;

            // ── Step 2: Lines (before & after upscale) ───────────────────────
            int compLimit = Math.Min(components.Count, MaxComponents);
            for (int ci = 0; ci < compLimit; ci++)
            {
                using var compImage = CropImage(blockImage, components[ci]);
                if (compImage == null) continue;

                SKImage? preprocessed = OcrPreprocessor.Process(compImage, opts.Preprocess);
                SKImage  workImage    = preprocessed ?? compImage;
                try
                {
                    var lineRegions = new LinearSegmentLinkingSplitter().Split(workImage);
                    if (lineRegions.Count == 0)
                    {
                        Console.WriteLine($"       comp{ci:D2}: 0 lines detected (skipped)");
                        continue;
                    }

                    var taken = lineRegions.Take(MaxLines).ToList();

                    // Two independent sets: one to display as-is, one to upscale
                    var linesBefore = taken.Select(r => (SKImage?)workImage.Subset(r)).ToList();
                    var linesAfter  = taken.Select(r => workImage.Subset(r)!).ToList();
                    OcrService.UpscaleLineImages(linesAfter, opts.Preprocess);

                    try
                    {
                        string fileBase = $"{prefix}_comp{ci:D2}";

                        SaveLinesComposite(linesBefore, outDir, $"{fileBase}_lines_pre.png",
                            $"BEFORE upscale — det{di} comp{ci}  ({taken.Count}/{lineRegions.Count} lines shown)");

                        SaveLinesComposite(linesAfter, outDir, $"{fileBase}_lines_post.png",
                            $"AFTER upscale  — det{di} comp{ci}  ({taken.Count}/{lineRegions.Count} lines shown)");

                        Console.WriteLine($"       comp{ci:D2}: {lineRegions.Count} line(s), " +
                                          $"showing {taken.Count}  →  {fileBase}_lines_pre/post.png");
                        savedFiles += 2;
                    }
                    finally
                    {
                        foreach (var img in linesBefore) img?.Dispose();
                        foreach (var img in linesAfter)  img?.Dispose();
                    }
                }
                finally
                {
                    preprocessed?.Dispose();
                }
            }
        }

        Console.WriteLine($"Total: {savedFiles} file(s) saved");
        Assert.That(savedFiles, Is.GreaterThan(0), "No files were saved — check detections and file paths.");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SkipIfModelMissing() =>
        Assume.That(File.Exists(ModelPath), Is.True,
            $"YOLO model not found: '{ModelPath}'. " +
            $"Set Integration:YoloClassifierOcrService:ModelPath in appsettings.local.json.");

    private static async Task<LayoutImage> ReadFirstPageAsync(string path)
    {
        var reader = new DocumentReader();
        await foreach (var page in reader.ReadDocumentAsync(path))
            return page;
        throw new InvalidOperationException($"No pages found in '{path}'");
    }

    private static SKImage? CropImage(SKImage source, BoundingBox bbox)
    {
        int x = Math.Max(0, (int)bbox.X);
        int y = Math.Max(0, (int)bbox.Y);
        int w = Math.Min((int)bbox.Width,  source.Width  - x);
        int h = Math.Min((int)bbox.Height, source.Height - y);
        if (w <= 0 || h <= 0) return null;
        return source.Subset(new SKRectI(x, y, x + w, y + h));
    }

    // ─── Word-segmentation diagnostic ────────────────────────────────────────

    /// <summary>
    /// For each detected block → Docstrum component → line, runs
    /// <see cref="OcrService.DetectWordsVerticalProjection"/> and saves a
    /// composite PNG showing each line with green word-boundary boxes and red
    /// dividers.  Use this to verify that word splitting is not over-segmenting
    /// characters within Cyrillic words.
    /// </summary>
    [Test]
    //[TestCase("PdfPath")]
    [TestCase("PdfScanPath1")]
    [TestCase("PdfScanPath2")]
    //[TestCase("ImagePath")]
    public async Task SaveWordSegmentationVisualizations(string documentKey)
    {
        SkipIfModelMissing();
        var docPath = GetConfigValue(documentKey);
        Assume.That(File.Exists(docPath), Is.True, $"File not found: '{docPath}'.");

        var opts    = ReadOcrOptions();
        var docName = Path.GetFileNameWithoutExtension(docPath);
        var outDir  = Path.Combine(ResultsBase, docName + "_words");
        Directory.CreateDirectory(outDir);

        using var page = await ReadFirstPageAsync(docPath);
        var sourceImage = page.CachedImage;
        Assume.That(sourceImage, Is.Not.Null, "Page has no CachedImage.");

        Console.WriteLine($"[{docName}]  {sourceImage!.Width}×{sourceImage.Height}px");
        Console.WriteLine($"Output: {outDir}");

        using var classifier = new YoloLayoutClassifier(ModelPath);
        var detections = (await classifier.ClassifyAsync(page)).ToList();
        Console.WriteLine($"Detections: {detections.Count}");

        int savedFiles = 0;

        for (int di = 0; di < detections.Count; di++)
        {
            var det = detections[di];
            using var blockImage = CropImage(sourceImage, det.BoundingBox);
            if (blockImage == null) continue;

            var safeLabel = det.Label.Replace(" ", "_");
            var prefix    = $"det{di:D2}_{safeLabel}";

            var components = OcrService.IsAtomicLabel(det.Label)
                ? [new BoundingBox(0, 0, blockImage.Width, blockImage.Height)]
                : DocstrumImageAnalyzer.ClusterComponents(blockImage);

            int compLimit = Math.Min(components.Count, MaxComponents);
            for (int ci = 0; ci < compLimit; ci++)
            {
                using var compImage = CropImage(blockImage, components[ci]);
                if (compImage == null) continue;

                SKImage? preprocessed = OcrPreprocessor.Process(compImage, opts.Preprocess);
                SKImage  workImage    = preprocessed ?? compImage;
                try
                {
                    var lineRegions = new LinearSegmentLinkingSplitter().Split(workImage);
                    if (lineRegions.Count == 0)
                    {
                        Console.WriteLine($"       comp{ci:D2}: 0 lines (skipped)");
                        continue;
                    }

                    var taken      = lineRegions.Take(MaxLines).ToList();
                    var lineImages = taken.Select(r => workImage.Subset(r)!).ToList();
                    OcrService.UpscaleLineImages(lineImages, opts.Preprocess);

                    try
                    {
                        string fileName = $"{prefix}_comp{ci:D2}_words.png";
                        int totalWords  = SaveLinesWithWordBoxes(lineImages, outDir, fileName,
                            $"Word segmentation (adaptive Otsu) — " +
                            $"det{di} comp{ci}  ({taken.Count}/{lineRegions.Count} lines shown)");

                        Console.WriteLine($"  det{di:D2} comp{ci:D2}: {lineRegions.Count} line(s), " +
                                          $"{totalWords} word(s) total → {fileName}");
                        savedFiles++;
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

        Console.WriteLine($"Total: {savedFiles} file(s) saved to {outDir}");
        Assert.That(savedFiles, Is.GreaterThan(0), "No files were saved — check detections and file paths.");
    }

    /// <summary>
    /// Saves a composite PNG: each row is one line image with green bounding
    /// boxes for each detected word and red vertical dividers between words.
    /// Returns the total number of word segments across all lines.
    /// </summary>
    private static int SaveLinesWithWordBoxes(
        IReadOnlyList<SKImage> lineImages,
        string outDir,
        string fileName,
        string title)
    {
        var valid = lineImages.Where(l => l != null).ToList();
        if (valid.Count == 0) return 0;

        const int headerH = 22;
        const int labelH  = 16;
        const int lineGap = 4;
        const int padding = 4;

        int maxW   = Math.Max(400, valid.Max(l => l.Width)) + padding * 2;
        int totalH = headerH + valid.Sum(l => labelH + l.Height + lineGap);

        using var surface = SKSurface.Create(new SKImageInfo(maxW, totalH));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(220, 220, 220));

        using var font      = new SKFont { Size = 11 };
        using var textWhite = new SKPaint { Color = SKColors.White,            IsAntialias = true };
        using var fillGray  = new SKPaint { Color = new SKColor(80, 80, 80),   Style = SKPaintStyle.Fill };
        using var fillBlue  = new SKPaint { Color = new SKColor(70, 130, 180), Style = SKPaintStyle.Fill };
        using var fillWhite = new SKPaint { Color = SKColors.White,            Style = SKPaintStyle.Fill };
        using var wordBox   = new SKPaint { Color = SKColors.Lime,             Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var divLine   = new SKPaint { Color = new SKColor(255, 60, 60),  Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

        // Header
        canvas.DrawRect(0, 0, maxW, headerH, fillGray);
        canvas.DrawText(title, padding, headerH - 5, SKTextAlign.Left, font, textWhite);

        int y          = headerH;
        int totalWords = 0;

        for (int i = 0; i < valid.Count; i++)
        {
            var line      = valid[i];
            var wordRects = new RlsaWordSplitter().Split(line);
            totalWords   += wordRects.Count;

            // Label bar
            canvas.DrawRect(0, y, maxW, labelH, fillBlue);
            canvas.DrawText(
                $"Line {i}:  {line.Width}×{line.Height}px  →  {wordRects.Count} word(s)",
                padding, y + labelH - 4, SKTextAlign.Left, font, textWhite);
            y += labelH;

            // White background + original line image
            canvas.DrawRect(padding, y, line.Width, line.Height, fillWhite);
            canvas.DrawImage(line, padding, y);

            // Green word bounding boxes
            foreach (var wr in wordRects)
                canvas.DrawRect(wr.Left + padding, y + wr.Top, wr.Width, wr.Height, wordBox);

            // Red vertical dividers at word boundaries
            for (int w = 1; w < wordRects.Count; w++)
            {
                float xDiv = wordRects[w].Left + padding - 0.5f;
                canvas.DrawLine(xDiv, y, xDiv, y + line.Height, divLine);
            }

            y += line.Height + lineGap;
        }

        Directory.CreateDirectory(outDir);
        using var snapshot = surface.Snapshot();
        using var data     = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(outDir, fileName), data.ToArray());

        return totalWords;
    }

    // Saves lines stacked vertically: each row = label + line image.
    private static void SaveLinesComposite(
        IEnumerable<SKImage?> lines,
        string outDir,
        string fileName,
        string title)
    {
        var valid = lines.Where(l => l != null).Cast<SKImage>().ToList();
        if (valid.Count == 0) return;

        const int headerH  = 22;
        const int labelH   = 16;
        const int lineGap  = 2;
        const int padding  = 4;

        int maxW   = Math.Max(300, valid.Max(l => l.Width)) + padding * 2;
        int totalH = headerH + valid.Sum(l => labelH + l.Height + lineGap);

        using var surface = SKSurface.Create(new SKImageInfo(maxW, totalH));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(220, 220, 220));

        using var font      = new SKFont { Size = 11 };
        using var textWhite = new SKPaint { Color = SKColors.White,     IsAntialias = true };
        using var textBlack = new SKPaint { Color = SKColors.Black,     IsAntialias = true };
        using var fillGray  = new SKPaint { Color = new SKColor(80, 80, 80),   Style = SKPaintStyle.Fill };
        using var fillBlue  = new SKPaint { Color = new SKColor(70, 130, 180), Style = SKPaintStyle.Fill };
        using var fillWhite = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };

        // Header
        canvas.DrawRect(0, 0, maxW, headerH, fillGray);
        canvas.DrawText(title, padding, headerH - 5, SKTextAlign.Left, font, textWhite);

        int y = headerH;
        for (int i = 0; i < valid.Count; i++)
        {
            var line = valid[i];

            // Label bar
            canvas.DrawRect(0, y, maxW, labelH, fillBlue);
            canvas.DrawText($"Line {i}:  {line.Width}×{line.Height}px", padding, y + labelH - 4,
                SKTextAlign.Left, font, textWhite);
            y += labelH;

            // White background + image
            canvas.DrawRect(0, y, maxW, line.Height, fillWhite);
            canvas.DrawImage(line, padding, y);
            y += line.Height + lineGap;
        }

        Directory.CreateDirectory(outDir);
        using var snapshot = surface.Snapshot();
        using var data     = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(outDir, fileName), data.ToArray());
    }
}
