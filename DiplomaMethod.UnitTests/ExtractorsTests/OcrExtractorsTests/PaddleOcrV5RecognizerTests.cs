using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests;

[TestFixture]
public class PaddleOcrRecognizerV5Tests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "TestData:Ocr";

    private PaddleOcrRecognizer_V5 _recognizer = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var modelPath = ResolveModelPath(Configuration["PaddleOcrV5:ModelPath"] ?? "models/eslav/rec.onnx");
        var dictPath  = ResolveModelPath(Configuration["PaddleOcrV5:DictPath"]  ?? "models/eslav/dict.txt");

        Assume.That(File.Exists(modelPath), Is.True,
            $"V5 eslav model not found: '{modelPath}'. " +
            $"Download from https://huggingface.co/monkt/paddleocr-onnx and set PaddleOcrV5:ModelPath in appsettings.local.json.");
        Assume.That(File.Exists(dictPath), Is.True,
            $"V5 eslav dict not found: '{dictPath}'. " +
            $"Download from https://huggingface.co/monkt/paddleocr-onnx and set PaddleOcrV5:DictPath in appsettings.local.json.");

        _recognizer = new PaddleOcrRecognizer_V5(new PaddleOcrRecognizerV5Options
        {
            ModelPath  = modelPath,
            DictPath   = dictPath,
            UseGpu     = bool.TryParse(Configuration["PaddleOcr:UseGpu"], out var gpu) && gpu,
            GpuId      = int.TryParse(Configuration["PaddleOcr:GpuId"], out var id) ? id : 0,
            // HuggingFace eslav dict has no space character — enable blank-run heuristic.
            // Set to 0 and UseSpaceChar=true when switching to official PP-OCRv5 models.
            WordSpaceBlankThreshold = int.TryParse(Configuration["PaddleOcrV5:WordSpaceBlankThreshold"], out var thr)
                ? thr : 4,
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _recognizer?.Dispose();

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    [TestCase("LineEN_1", "Intense research on Visual Simultaneous Localization and")]
    [TestCase("LineEN_2", "filing, Third Avenue Management")]
    [TestCase("LineEN_3", "Long ago, the PJD")]
    [TestCase("LineRU_1", "межуточного и итогового тестирования")]
    [TestCase("LineRU_2", "формации и др. в обучающей программе.")]
    [TestCase("LineRU_3", "Экономика и математика являются неотъемлемыми и взаимосвязанными")]
    [TestCase("LineRU_4", "На территории Мордовия традиционными")]
    [TestCase("LineRU_5", "община. Сейчас она насчитывает около 200 че-")]
    [TestCase("LineRU_6", "И.А. Лошкарев, доцент кафедры нормальной анатомии")]
    [TestCase("LineRU_7", "Византия. В период раннего средневековья об-")]
    [TestCase("LineRU_8", "4. Маврин С. А. Педагогические системы и")]
    [TestCase("LineRU_9", "лективе авторов АОС, в который обязатель-")]
    public async Task RecognizeLineAsync_ShouldRecognizeTextCorrectly(string configKey, string expectedText)
    {
        var imagePath = GetConfigValue(configKey);
        Assume.That(File.Exists(imagePath), Is.True, $"Test image not found: '{imagePath}'.");

        var image = await OcrUtils.GetSKImageAsync(imagePath);

        var result = await _recognizer.RecognizeLineAsync(image);

        Assert.That(result, Is.EqualTo(expectedText));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var assemblyDir = Path.GetDirectoryName(typeof(PaddleOcrRecognizerV5Tests).Assembly.Location)!;
        return Path.Combine(assemblyDir, path);
    }
}
