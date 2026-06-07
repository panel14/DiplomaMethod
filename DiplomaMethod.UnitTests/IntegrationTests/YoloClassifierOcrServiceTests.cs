using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiplomaMethod.Application.Classifiers;
using DiplomaMethod.Application.DocumentReader;
using DiplomaMethod.Application.Extractors.Ocr;
using DiplomaMethod.Application.Diagnostics;
using DiplomaMethod.Application.Extractors.Ocr.Postprocessors;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Line;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Window;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;

namespace DiplomaMethod.UnitTests.IntegrationTests;

[TestFixture]
[Category("Integration")]
public class YoloClassifierOcrServiceTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "Integration:YoloClassifierOcrService";

    private string ModelPath   => GetConfigValue("ModelPath");
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
        _ocrService = new OcrService(ReadOcrOptions(), paddleOptions, 
        lineSplitter: new LinearSegmentLinkingSplitter(),
        windowSplitter: new LineWindowSplitter());
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var assemblyDir = Path.GetDirectoryName(typeof(YoloClassifierOcrServiceTests).Assembly.Location)!;
        return Path.Combine(assemblyDir, path);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _ocrService.Dispose();

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    //[TestCase("PdfPath")]
    [TestCase("PdfScanPath1")]
    [TestCase("PdfScanPath2")]
    //[TestCase("ImagePath")]
    //[TestCase("PdfPathRuDirect")]
    public async Task ClassifyAndRead_FirstPage_SavesJson(string documentKey)
    {
        SkipIfModelMissing();
        var path = GetConfigValue(documentKey);
        Assume.That(File.Exists(path), Is.True, $"Test file not found: '{path}'.");

        var sw = Stopwatch.StartNew();

        using var page = await ReadFirstPageAsync(path);
        int imgWidth  = page.CachedImage!.Width;
        int imgHeight = page.CachedImage!.Height;

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Page rendered: {imgWidth}×{imgHeight}px  " +
                          $"PDF: {page.Page.PdfPageWidthPt:F0}×{page.Page.PdfPageHeightPt:F0}pt");

        using var classifier = new YoloLayoutClassifier(ModelPath);
        var detections = (await classifier.ClassifyAsync(page)).ToList();

        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Detected: {detections.Count} region(s)");
        foreach (var d in detections)
            Console.WriteLine($"  [{d.ClassIndex,2}] {d.Label,-16} conf={d.Confidence:F2}  " +
                              $"box=({d.BoundingBox.X:F0},{d.BoundingBox.Y:F0} " +
                              $"{d.BoundingBox.Width:F0}×{d.BoundingBox.Height:F0})");

        IReadOnlyList<DetectionResult> effectiveDetections = detections.Count > 0
            ? detections
            : [new DetectionResult { Label = "Text", Confidence = 1.0, BoundingBox = new BoundingBox(0, 0, imgWidth, imgHeight) }];

        var blocks = await _ocrService.ReadAsync(page, effectiveDetections);

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
            Assert.That(blocks, Is.Not.Empty, "Pipeline must produce at least one TextBlock");
            Assert.That(File.Exists(fullPath));
            Assert.That(File.Exists(simplePath));
        }
    }

    // TEMPORARY: profiles where the Window-mode OCR time goes. Run manually:
    //   dotnet test --filter "FullyQualifiedName~Profile_WindowMode"
    [Test]
    [Explicit("Profiling — run manually")]
    [Category("Profiling")]
    public async Task Profile_WindowMode()
    {
        SkipIfModelMissing();
        var path = GetConfigValue("PdfScanPath1");
        Assume.That(File.Exists(path), Is.True, $"Test file not found: '{path}'.");

        var paddleOptions = new PaddleOcrRecognizerV5Options
        {
            ModelPath = ResolveModelPath(Configuration["PaddleOcrV5:ModelPath"] ?? "models/eslav/rec.onnx"),
            DictPath  = ResolveModelPath(Configuration["PaddleOcrV5:DictPath"]  ?? "models/eslav/dict.txt"),
            UseGpu    = bool.TryParse(Configuration["PaddleOcr:UseGpu"], out var gpu) && gpu,
        };
        Assume.That(File.Exists(paddleOptions.ModelPath), Is.True,
            $"PaddleV5 model not found: '{paddleOptions.ModelPath}'.");

        var ocrOptions = new OcrOptions { WordSegmentation = WordSegmentationMode.Window, MinPaddleScore = 0.8f };
        using var ocr = new OcrService(ocrOptions, paddleOptions,
            lineSplitter:      new LinearSegmentLinkingSplitter(topPaddingPx: 4),
            wordSplitter:      new CcaWordSplitter(),
            linePostprocessor: new MedianHeightLineFilter(),
            windowSplitter:    new LineWindowSplitter());

        using var classifier = new YoloLayoutClassifier(ModelPath);
        var reader = new DocumentReader();

        Console.WriteLine($"GPU requested: {paddleOptions.UseGpu}");

        // Warm-up: first inference pays JIT + CUDA init — excluded from the measured numbers.
        await foreach (var warm in reader.ReadDocumentAsync(path))
        {
            using (warm)
            {
                var d0 = (await classifier.ClassifyAsync(warm)).ToList();
                await ocr.ReadAsync(warm, d0);
            }
            break;
        }

        OcrProfiler.Reset();
        OcrProfiler.Enabled = true;
        var ocrWall = TimeSpan.Zero;
        int pages = 0, blocksTotal = 0;

        await foreach (var page in reader.ReadDocumentAsync(path))
        {
            using (page)
            {
                var dets = (await classifier.ClassifyAsync(page)).ToList(); // not counted in ocrWall
                var w0 = Stopwatch.StartNew();
                var blocks = await ocr.ReadAsync(page, dets);
                w0.Stop();
                ocrWall += w0.Elapsed;
                blocksTotal += blocks.Count;
                pages++;
            }
        }
        OcrProfiler.Enabled = false;

        string providers = string.Join(", ", Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders());
        string report = $"Document: {Path.GetFileName(path)} | pages: {pages} | blocks: {blocksTotal}\n" +
                        $"GPU requested: {paddleOptions.UseGpu} | ORT providers: [{providers}]\n" +
                        OcrProfiler.Report(ocrWall);
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ocr_profile.txt"), report);

        Assert.That(pages, Is.GreaterThan(0));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SkipIfModelMissing()
    {
        Assume.That(File.Exists(ModelPath), Is.True,
            $"YOLO model not found: '{ModelPath}'. " +
            $"Set Integration:YoloClassifierOcrService:ModelPath in appsettings.local.json.");
    }

    private static async Task<LayoutImage> ReadFirstPageAsync(string path)
    {
        var reader = new DocumentReader();
        await foreach (var page in reader.ReadDocumentAsync(path))
            return page;
        throw new InvalidOperationException($"No pages found in '{path}'");
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
