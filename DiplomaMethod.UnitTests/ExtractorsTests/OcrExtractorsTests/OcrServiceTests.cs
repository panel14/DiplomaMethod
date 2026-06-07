using System.Text.Json;
using DiplomaMethod.Application.Extractors.Ocr;
using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests;

[TestFixture]
public class OcrServiceTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "TestData:Ocr";

    private OcrService _service = null!;

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
        _service = new OcrService(new OcrOptions(), paddleOptions);
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var assemblyDir = Path.GetDirectoryName(typeof(OcrServiceTests).Assembly.Location)!;
        return Path.Combine(assemblyDir, path);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _service.Dispose();

    private string ResultsPath
    {
        get
        {
            var raw = Configuration["TestResults:OcrService"] ?? "TestResults/OcrService";
            if (Path.IsPathRooted(raw)) return raw;
            var asmDir = Path.GetDirectoryName(typeof(OcrServiceTests).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            return Path.Combine(asmDir, raw);
        }
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    [TestCase("PageEN_1")]
    [TestCase("PageEN_2")]
    [TestCase("PageEN_Scan")]
    [TestCase("PageRU_HQ")]
    [TestCase("PageRU_LQ")]
    [TestCase("PageEN_MQ_NoHead")]
    public async Task ReadAsync_FullPageAsSingleBlock_ExtractsLines(string configKey)
    {
        var imagePath = GetConfigValue(configKey);
        var skImage = await OcrUtils.GetSKImageAsync(imagePath);

        int imgWidth  = skImage.Width;
        int imgHeight = skImage.Height;
        using var layoutImage = new LayoutImage { CachedImage = skImage };

        var detection = new List<DetectionResult>
        {
            new()
            {
                Label = "Text",
                Confidence = 1.0,
                BoundingBox = new BoundingBox(0, 0, imgWidth, imgHeight)
            }
        };

        var result = await _service.ReadAsync(layoutImage, detection);

        Console.WriteLine($"[{configKey}]  {imgWidth}×{imgHeight}px  → {result.Count} block(s)");
        foreach (var (block, i) in result.Select((b, i) => (b, i)))
        {
            Console.WriteLine($"  [{i}] label={block.Label}  conf={block.Confidence:F2}  lines={block.Lines.Count}");
            foreach (var line in block.Lines)
                Console.WriteLine($"    \"{Truncate(line.Text, 80)}\"");
        }

        Directory.CreateDirectory(ResultsPath);
        var jsonPath = Path.Combine(ResultsPath, $"ocr_{configKey}.json");
        var payload = new
        {
            image = configKey,
            width = imgWidth,
            height = imgHeight,
            blocks = result.Select((b, i) => new
            {
                index = i,
                label = b.Label,
                confidence = Math.Round(b.Confidence, 3),
                lines = b.Lines.Select(l => l.Text).ToArray()
            }).ToArray()
        };
        await File.WriteAllTextAsync(jsonPath,
            JsonSerializer.Serialize(payload, JsonOptions));
        Console.WriteLine($"  Saved → {jsonPath}");

        Assert.That(result, Is.Not.Empty, $"OcrService must produce at least one TextBlock for '{configKey}'");
        Assert.That(result[0].Lines, Is.Not.Empty, "TextBlock must contain at least one line");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Truncate(string? s, int max) =>
        s == null ? "" : s.Length <= max ? s : s[..max] + "…";
}
