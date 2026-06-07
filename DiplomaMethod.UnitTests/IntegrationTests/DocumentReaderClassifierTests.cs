using DiplomaMethod.Application.Classifiers;
using DiplomaMethod.Application.DocumentReader;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.UnitTests.Base;
using SkiaSharp;

namespace DiplomaMethod.UnitTests.IntegrationTests;

[TestFixture]
[Category("Integration")]
public class DocumentReaderClassifierTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "Integration:DocumentReaderClassifier";

    private string ModelPath   => GetConfigValue("ModelPath");
    private string PdfPath     => GetConfigValue("PdfPath");
    private string ResultsPath => GetConfigValue("ResultsPath");

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    public async Task ReadFirstPage_Classify_SavesVisualization()
    {
        SkipIfModelMissing();

        var reader = new DocumentReader();
        using var classifier = new YoloLayoutClassifier(ModelPath);

        LayoutImage? firstPage = null;
        await foreach (var page in reader.ReadDocumentAsync(PdfPath))
        {
            firstPage = page;
            break;
        }

        Assert.That(firstPage, Is.Not.Null, "Document must have at least one page");
        Assert.That(firstPage!.CachedImage, Is.Not.Null, "Page must have a rendered image");

        var detections = (await classifier.ClassifyAsync(firstPage)).ToList();

        PrintDetections(firstPage, detections);

        string outputPath = SaveVisualization(
            firstPage.CachedImage!, detections, ResultsPath, "page01_classified.png");

        Console.WriteLine($"Saved → {outputPath}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detections, Is.Not.Empty, "Expected at least one detection on first page");
            Assert.That(File.Exists(outputPath));
        }

    }

    [Test]
    public async Task ReadAllPages_Classify_SavesVisualizationPerPage()
    {
        SkipIfModelMissing();

        var reader = new DocumentReader();
        using var classifier = new YoloLayoutClassifier(ModelPath);

        int totalPages      = 0;
        int totalDetections = 0;
        int pagesWithHits   = 0;

        await foreach (var page in reader.ReadDocumentAsync(PdfPath))
        {
            var detections = (await classifier.ClassifyAsync(page)).ToList();
            PrintDetections(page, detections);

            string fileName = $"page{page.Page.PageNumber:D2}_classified.png";
            SaveVisualization(page.CachedImage!, detections, ResultsPath, fileName);

            totalPages++;
            totalDetections += detections.Count;
            if (detections.Count > 0) pagesWithHits++;
        }

        Console.WriteLine($"\nSummary: {totalPages} pages, {totalDetections} total detections, " +
                          $"{pagesWithHits}/{totalPages} pages with detections");
        Console.WriteLine($"Output folder: {ResultsPath}");

        Assert.That(totalPages,    Is.GreaterThan(0), "Document must have pages");
        Assert.That(pagesWithHits, Is.GreaterThan(0), "At least one page must have detections");
    }

    // ─── Diagnostic ──────────────────────────────────────────────────────────

    /// <summary>
    /// Renders only the first PDF page and saves it as PNG for visual inspection.
    /// Use this to verify that DocumentReader produces a correct image before debugging classifier output.
    /// </summary>
    [Test]
    public async Task RenderFirstPage_SavesPng_ForVisualInspection()
    {
        Assume.That(File.Exists(PdfPath), Is.True, $"Test PDF not found: '{PdfPath}'");

        var reader = new DocumentReader();
        LayoutImage? firstPage = null;
        await foreach (var page in reader.ReadDocumentAsync(PdfPath))
        {
            firstPage = page;
            break;
        }

        Assert.That(firstPage?.CachedImage, Is.Not.Null, "DocumentReader must return a rendered page");

        var img = firstPage!.CachedImage!;
        Console.WriteLine($"Rendered page: {img.Width}×{img.Height}px  " +
                          $"(PDF size: {firstPage.Page.PdfPageWidthPt:F0}×{firstPage.Page.PdfPageHeightPt:F0}pt)");

        Directory.CreateDirectory(ResultsPath);
        string outputPath = Path.Combine(ResultsPath, "rendered_page01.png");

        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        await using var fs = File.Create(outputPath);
        data.AsStream().CopyTo(fs);

        Console.WriteLine($"Saved → {outputPath}");
        Assert.That(File.Exists(outputPath));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SkipIfModelMissing()
    {
        Assume.That(File.Exists(ModelPath), Is.True,
            $"YOLO model not found at '{ModelPath}'. Set Integration:DocumentReaderClassifier:ModelPath in appsettings.local.json.");
        Assume.That(File.Exists(PdfPath), Is.True,
            $"Test PDF not found at '{PdfPath}'.");
    }

    private static void PrintDetections(LayoutImage page, List<DetectionResult> detections)
    {
        var img = page.CachedImage;
        Console.WriteLine($"[Page {page.Page.PageNumber}] " +
                          $"{img?.Width}×{img?.Height}px  →  {detections.Count} detection(s)");
        foreach (var d in detections)
            Console.WriteLine($"  [{d.ClassIndex,2}] {d.Label,-16} conf={d.Confidence:F2}  " +
                              $"box=({d.BoundingBox.X:F0},{d.BoundingBox.Y:F0} " +
                              $"{d.BoundingBox.Width:F0}×{d.BoundingBox.Height:F0})");
    }

    private static string SaveVisualization(
        SKImage source,
        IReadOnlyList<DetectionResult> detections,
        string outputDir,
        string fileName)
    {
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, fileName);

        using var surface = SKSurface.Create(
            new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawImage(source, 0, 0);

        using var font      = new SKFont { Size = 16 };
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var boxPaint  = new SKPaint { IsAntialias = false };

        foreach (var det in detections)
        {
            var box   = det.BoundingBox;
            float x   = (float)box.X;
            float y   = (float)box.Y;
            float w   = (float)box.Width;
            float h   = (float)box.Height;
            var color = ColorForLabel(det.Label);

            boxPaint.Color     = color.WithAlpha(200);
            boxPaint.Style     = SKPaintStyle.Stroke;
            boxPaint.StrokeWidth = 2;
            canvas.DrawRect(x, y, w, h, boxPaint);

            string label  = $"{det.Label} {det.Confidence:P0}";
            float textW   = font.MeasureText(label);
            float textH   = font.Size + 4;

            boxPaint.Style = SKPaintStyle.Fill;
            boxPaint.Color = color.WithAlpha(200);
            canvas.DrawRect(x, y - textH, textW + 6, textH, boxPaint);
            canvas.DrawText(label, x + 3, y - 3, SKTextAlign.Left, font, textPaint);
        }

        using var img  = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs   = File.OpenWrite(outputPath);
        data.SaveTo(fs);

        return outputPath;
    }

    private static SKColor ColorForLabel(string label) => label switch
    {
        "Text"           => SKColors.DodgerBlue,
        "Title"          => SKColors.OrangeRed,
        "List"           => SKColors.MediumSeaGreen,
        "Table"          => SKColors.MediumOrchid,
        "Figure"         => SKColors.Goldenrod,
        "Section Header" => SKColors.DarkOrange,
        "Page Header"    => SKColors.SlateBlue,
        "Page Footer"    => SKColors.SlateBlue,
        "Footnote"       => SKColors.RosyBrown,
        "Formula"        => SKColors.Teal,
        "Caption"        => SKColors.DarkKhaki,
        _                => SKColors.Gray
    };
}
