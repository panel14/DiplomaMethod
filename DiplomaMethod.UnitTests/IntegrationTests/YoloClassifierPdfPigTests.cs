using DiplomaMethod.Application.Adapters;
using DiplomaMethod.Application.Classifiers;
using DiplomaMethod.Application.DocumentReader;
using DiplomaMethod.Application.Extractors.Direct;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Strong;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Threshold;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiplomaMethod.UnitTests.IntegrationTests;

[TestFixture]
[Category("Integration")]
public class YoloClassifierPdfPigTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "Integration:YoloClassifierPdfPig";

    private string ModelPath   => GetConfigValue("ModelPath");
    private string PdfPath     => GetConfigValue("PdfPath");
    private string ResultsPath => GetConfigValue("ResultsPath");

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    public async Task ClassifyAndExtract_FirstPage_SavesJson()
    {
        SkipIfFilesAreMissing();
        var sw = Stopwatch.StartNew();

        using var image = await BuildPdfLayoutImageAsync(PdfPath, pageNumber: 1);
        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Page rendered: " +
                          $"{image.CachedImage?.Width}×{image.CachedImage?.Height}px  " +
                          $"PDF: {image.Page.PdfPageWidthPt:F0}×{image.Page.PdfPageHeightPt:F0}pt");

        // Classify — returns boxes in original image pixel coordinates
        using var classifier = new YoloLayoutClassifier(ModelPath);
        var classifierOptions = new ClassifierOptions();
        var imageDetections = (await classifier.ClassifyAsync(image)).ToList();

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Classified: {imageDetections.Count} region(s)");
        foreach (var d in imageDetections)
            Console.WriteLine($"  [{d.ClassIndex,2}] {d.Label,-16} conf={d.Confidence:F2}  " +
                              $"box=({d.BoundingBox.X:F0},{d.BoundingBox.Y:F0} " +
                              $"{d.BoundingBox.Width:F0}×{d.BoundingBox.Height:F0})");

        Assert.That(imageDetections, Is.Not.Empty, "Classifier must detect at least one region");

        // Map image-pixel → PDF-point coordinates (PdfPig uses bottom-left origin)
        var adapter = CoordinateAdapter.ForImagePixels(image, classifierOptions);
        var pdfDetections = adapter.MapToPdfSpace(
            imageDetections, image.Page.PdfPageWidthPt, image.Page.PdfPageHeightPt);

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Extracting text via PdfPig...");

        // Extract text
        var extractor = new PdfPigExtractorService(BuildTextValidator());
        var blocks = await extractor.ReadAsync(image, pdfDetections);

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Extracted: {blocks.Count} block(s)");
        foreach (var b in blocks)
            Console.WriteLine($"  [{b.Label,-16}] conf={b.Confidence:F2}  lines={b.Lines.Count}  " +
                              $"\"{Truncate(b.Lines.FirstOrDefault()?.Text, 60)}\"");

        var fileName = Path.GetFileNameWithoutExtension(PdfPath);
        string detailedPath = SaveDetailedJson(blocks, PdfPath, ResultsPath, $"page01_extracted_{fileName}.json", sw.Elapsed);
        string simplePath   = SaveSimpleJson(blocks, PdfPath, ResultsPath, $"page01_extracted_{fileName}_simple.json", sw.Elapsed);
        Console.WriteLine($"Detailed → {detailedPath}");
        Console.WriteLine($"Simple   → {simplePath}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks, Is.Not.Empty, "PdfPig must extract at least one text block");
            Assert.That(File.Exists(detailedPath));
            Assert.That(File.Exists(simplePath));
        }

    }

    private void SkipIfFilesAreMissing()
    {
        Assume.That(File.Exists(ModelPath), Is.True,
            $"YOLO model not found: '{ModelPath}'. " +
            $"Set Integration:YoloClassifierPdfPig:ModelPath in appsettings.local.json.");
        Assume.That(File.Exists(PdfPath), Is.True,
            $"Test PDF not found: '{PdfPath}'. " +
            $"Set Integration:YoloClassifierPdfPig:PdfPath in appsettings.local.json.");
    }

    private static async Task<LayoutImage> BuildPdfLayoutImageAsync(string pdfPath, int pageNumber = 1)
    {
        var pdfBytes  = await File.ReadAllBytesAsync(pdfPath);
        var pdfStream = new MemoryStream(pdfBytes);

        var reader = new DocumentReader();
        LayoutImage? rendered = null;
        await foreach (var page in reader.ReadDocumentAsync(pdfPath))
        {
            if (page.Page.PageNumber == pageNumber)
            {
                rendered = page;
                break;
            }
        }

        if (rendered == null)
            throw new InvalidOperationException($"Page {pageNumber} not found in '{pdfPath}'");

        rendered.ImageStream = pdfStream;
        return rendered;
    }

    private static TextValidator BuildTextValidator() =>
        new(
            [new ControlCharHeuristic(0.05), new WordOverlapHeuristic()],
            [new LineCountHeuristic(), new UpperSymbolsThreshold()]);

    private static string SaveDetailedJson(
        IReadOnlyList<TextBlock> blocks,
        string inputFile,
        string outputDir,
        string fileName,
        TimeSpan elapsed)
    {
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, fileName);

        var output = new ExtractionOutput(
            InputFile:      Path.GetFullPath(inputFile),
            ProcessedAt:    DateTime.UtcNow.ToString("O"),
            ElapsedSeconds: Math.Round(elapsed.TotalSeconds, 2),
            BlockCount:     blocks.Count,
            Blocks: [.. blocks.Select(b => new BlockOutput(
                Label:      b.Label,
                Confidence: Math.Round(b.Confidence, 4),
                Box:        new BoxOutput(b.Box.X, b.Box.Y, b.Box.Width, b.Box.Height),
                Lines: [.. b.Lines.Select(l => new LineOutput(
                    l.Text,
                    new BoxOutput(l.Box.X, l.Box.Y, l.Box.Width, l.Box.Height)))]))]);

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOpts));
        return outputPath;
    }

    private static string SaveSimpleJson(
        IReadOnlyList<TextBlock> blocks,
        string inputFile,
        string outputDir,
        string fileName,
        TimeSpan elapsed)
    {
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, fileName);

        var output = new SimpleExtractionOutput(
            InputFile:      Path.GetFullPath(inputFile),
            ProcessedAt:    DateTime.UtcNow.ToString("O"),
            ElapsedSeconds: Math.Round(elapsed.TotalSeconds, 2),
            BlockCount:     blocks.Count,
            Blocks: [.. blocks.Select(b => new SimpleBlockOutput(
                Label:      b.Label,
                Confidence: Math.Round(b.Confidence, 4),
                Box:        new BoxOutput(b.Box.X, b.Box.Y, b.Box.Width, b.Box.Height),
                Text:       b.Accumulate()))]);

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOpts));
        return outputPath;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        Encoder                = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Truncate(string? s, int max) =>
        s == null ? "" : s.Length <= max ? s : s[..max] + "…";

    // ─── JSON DTOs (same schema as Program.cs ExtractionOutput) ──────────────

    record ExtractionOutput(
        string        InputFile,
        string        ProcessedAt,
        double        ElapsedSeconds,
        int           BlockCount,
        BlockOutput[] Blocks);

    record BlockOutput(
        string       Label,
        double       Confidence,
        BoxOutput    Box,
        LineOutput[] Lines);

    record LineOutput(string Text, BoxOutput Box);

    record BoxOutput(double X, double Y, double Width, double Height);

    record SimpleExtractionOutput(
        string             InputFile,
        string             ProcessedAt,
        double             ElapsedSeconds,
        int                BlockCount,
        SimpleBlockOutput[] Blocks);

    record SimpleBlockOutput(
        string    Label,
        double    Confidence,
        BoxOutput Box,
        string    Text);
}
