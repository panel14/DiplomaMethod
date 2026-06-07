using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests
{
    [TestFixture]
    public class TesseractOcrRecognizerTests : BaseUnitTest
    {
        // Reuse the shared test-image paths via the same section as PaddleOcrRecognizerTests
        public override string BaseConfigSection { get; set; } = "TestData:Ocr";
        private TesseractOcrRecognizer _recognizer;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var options = new TesseractOptions
            {
                TessDataPath = ResolvePath(Configuration["Tesseract:TessDataPath"] ?? "tessdata"),
                Language = Configuration["Tesseract:Language"] ?? "rus",
                PageSegMode = int.TryParse(Configuration["Tesseract:PageSegMode"], out var psm) ? psm : 7,
                UpscaleMinHeight = int.TryParse(Configuration["Tesseract:UpscaleMinHeight"], out var h) ? h : 32,
            };
            _recognizer = new TesseractOcrRecognizer(options);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _recognizer.Dispose();
        }

        [Test]
        [TestCase("LineRu_1", "межуточного и итогового тестирования")]
        [TestCase("LineRu_2", "формации и др. в обучающей программе.")]
        [TestCase("LineEn",   "Intense research on Visual Simultaneous Localization and")]
        public async Task TesseractOcrRecognizer_ShouldRecognizeTextLineCorrectly(string configKey, string expectedSubstring)
        {
            var imagePath = GetConfigValue(configKey);
            var image = await OcrUtils.GetSKImageAsync(imagePath);

            var result = await _recognizer.RecognizeLineAsync(image);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Not.Empty);
            Assert.That(result, Does.Contain(expectedSubstring));
        }

        private string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            var assemblyDir = Path.GetDirectoryName(GetType().Assembly.Location)!;
            return Path.Combine(assemblyDir, path);
        }
    }
}
