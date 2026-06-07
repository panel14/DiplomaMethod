using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests
{
    [TestFixture]
    public class PaddleOcrRecognizerV3Tests : BaseUnitTest
    {
        public override string BaseConfigSection { get; set; } = "TestData:Ocr";
        private PaddleOcrRecognizer_V3 _recognizer;

        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            var options = new PaddleOcrRecognizerV3Options
            {
                UseGpu = bool.TryParse(Configuration["PaddleOcr:UseGpu"], out var gpu) && gpu,
                GpuId = int.TryParse(Configuration["PaddleOcr:GpuId"], out var id) ? id : 0,
                GpuInitialMemoryMb = int.TryParse(Configuration["PaddleOcr:GpuInitialMemoryMb"], out var mem) ? mem : 500,
            };
            _recognizer = new PaddleOcrRecognizer_V3(options);
        }

        [TearDown]
        public void TearDown()
        {
            _recognizer.Dispose();
        }

        [Test]
        [TestCase("LineEn_1", "Intense research on Visual Simultaneous Localization and")]
        [TestCase("LineEn_2", "filing, Third Avenue Management")]
        [TestCase("LineEn_3", "Long ago, the PJD")]
        [TestCase("LineRu_1", "межуточного и итогового тестирования")]
        [TestCase("LineRu_2", "формации и др. в обучающей программе.")]
        public async Task PaddleOcrRecognizer_ShouldRecognizeTextLineCorrectly(string configPath, string expectedText)
        {
            var imagePath = GetConfigValue(configPath);
            var image = await OcrUtils.GetSKImageAsync(imagePath);

            var result = await _recognizer.RecognizeLineAsync(image);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.EqualTo(expectedText));
        }
    }
}
