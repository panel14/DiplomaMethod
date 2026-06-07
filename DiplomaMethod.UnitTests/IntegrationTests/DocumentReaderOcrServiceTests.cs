using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiplomaMethod.Application.DocumentReader;
using DiplomaMethod.Application.Extractors.Ocr;
using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;

namespace DiplomaMethod.UnitTests.IntegrationTests;

[TestFixture]
[Category("Integration")]
public class DocumentReaderOcrServiceTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "Integration:DocumentReaderOcrService";
    private string ResultsPath => GetConfigValue("ResultsPath");

    private OcrService _ocrService = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var paddleOptions = new PaddleOcrRecognizerV5Options
        {
            ModelPath               = ResolveModelPath(Configuration["PaddleOcrV5:ModelPath"] ?? "models/eslav/rec.onnx"),
            DictPath                = ResolveModelPath(Configuration["PaddleOcrV5:DictPath"]  ?? "models/eslav/dict.txt"),
            UseGpu                  = bool.TryParse(Configuration["PaddleOcr:UseGpu"], out var gpu) && gpu,
            GpuId                   = int.TryParse(Configuration["PaddleOcr:GpuId"], out var id) ? id : 0,
            WordSpaceBlankThreshold = int.TryParse(Configuration["PaddleOcrV5:WordSpaceBlankThreshold"], out var thr) ? thr : 0,
        };
        _ocrService = new OcrService(ReadOcrOptions(), paddleOptions);
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var assemblyDir = Path.GetDirectoryName(typeof(DocumentReaderOcrServiceTests).Assembly.Location)!;
        return Path.Combine(assemblyDir, path);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _ocrService.Dispose();

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    [TestCase("PdfPath")]
    [TestCase("PdfScanPath")]
    [TestCase("ImagePath")]
    public async Task ReadFirstPage_ExtractsBlocks_SavesJson(string document)
    {
        var sw = Stopwatch.StartNew();

        var path = GetConfigValue(document);
        using var page = await ReadFirstPageAsync(path);
        int imgWidth  = page.CachedImage!.Width;
        int imgHeight = page.CachedImage!.Height;

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Page rendered: {imgWidth}×{imgHeight}px  " +
                          $"PDF: {page.Page.PdfPageWidthPt:F0}×{page.Page.PdfPageHeightPt:F0}pt");

        var detection = new List<DetectionResult>
        {
            new() { Label = "Text", Confidence = 1.0, BoundingBox = new BoundingBox(0, 0, imgWidth, imgHeight) }
        };

        var blocks = await _ocrService.ReadAsync(page, detection);

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Extracted: {blocks.Count} block(s)");
        foreach (var b in blocks)
            Console.WriteLine($"  [{b.Label,-16}] conf={b.Confidence:F2}  lines={b.Lines.Count}  " +
                              $"\"{Truncate(b.Lines.FirstOrDefault()?.Text, 60)}\"");

        var baseName    = $"page01_{Path.GetFileNameWithoutExtension(path)}";
        string fullPath   = SaveDetailedJson(blocks, path, ResultsPath, $"{baseName}.json", sw.Elapsed);
        string simplePath = SaveSimpleJson(blocks, path, ResultsPath, $"{baseName}_simple.json", sw.Elapsed);

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Full   → {fullPath}");
        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Simple → {simplePath}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks, Is.Not.Empty, "OcrService must produce at least one TextBlock");
            Assert.That(File.Exists(fullPath));
            Assert.That(File.Exists(simplePath));
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<LayoutImage> ReadFirstPageAsync(string pdfPath)
    {
        var reader = new DocumentReader();
        await foreach (var page in reader.ReadDocumentAsync(pdfPath))
            return page;
        throw new InvalidOperationException($"No pages found in '{pdfPath}'");
    }

    private static string Truncate(string? s, int max) =>
        s == null ? "" : s.Length <= max ? s : s[..max] + "…";

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

    // ─── JSON DTOs ────────────────────────────────────────────────────────────

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

    record SimpleExtractionOutput(
        string              InputFile,
        string              ProcessedAt,
        double              ElapsedSeconds,
        int                 BlockCount,
        SimpleBlockOutput[] Blocks);

    record SimpleBlockOutput(
        string    Label,
        double    Confidence,
        BoxOutput Box,
        string    Text);

    record BoxOutput(double X, double Y, double Width, double Height);
}
