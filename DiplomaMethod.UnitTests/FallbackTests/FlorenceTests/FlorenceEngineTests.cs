using DiplomaMethod.Application.Extractors.Fallback.Florence;
using DiplomaMethod.Core.Options;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;

namespace DiplomaMethod.UnitTests.FallbackTests.FlorenceTests
{
    [TestFixture]
    public class FlorenceEnginerTests : BaseUnitTest
    {
        public override string BaseConfigSection { get; set; } = "TestData:Ocr";
        private FlorenceEngine _recognizer;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            var options = new FlorenceOcrOptions
            {
                ModelPath = ResolvePath(Configuration["FlorenceOcr:ModelPath"] ?? "models/florence2"),
                AutoDownload = !bool.TryParse(Configuration["FlorenceOcr:AutoDownload"], out var ad) || ad,
            };
            _recognizer = await FlorenceEngine.CreateAsync(options);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _recognizer.Dispose();
        }

        [Test]
        [TestCase("LineEn", "Intense research on Visual Simultaneous Localization and")]
        [TestCase("LineRu_1", "межуточного и итогового тестирования")]
        [TestCase("LineRu_2", "формации и др. в обучающей программе.")]
        public async Task FlorenceOcrRecognizer_ShouldRecognizeLineText(string configKey, string expectedSubstring)
        {
            var imagePath = GetConfigValue(configKey);
            var image = await OcrUtils.GetSKImageAsync(imagePath);

            var result = await _recognizer.RecognizeLineAsync(image);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Not.Empty);
            Assert.That(result, Does.Contain(expectedSubstring));
        }

        [Test]
        [TestCase("PageEN_1", "This paper presents ORB-SLAM3")]
        public async Task FlorenceOcrRecognizer_ShouldRecognizeBlockText(string configKey, string expectedSubstring)
        {
            var imagePath = GetConfigValue(configKey);
            var imageStream = File.OpenRead(imagePath);

            var result = await _recognizer.RecognizeBlockAsync(imageStream);

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
