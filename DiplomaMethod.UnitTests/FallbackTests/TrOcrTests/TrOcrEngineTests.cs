using DiplomaMethod.Application.Extractors.Fallback.TrOcr;
using DiplomaMethod.Application.Utils.TrOcr;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Options.TrOcr;
using DiplomaMethod.UnitTests.Base;

namespace DiplomaMethod.UnitTests.TrOcrTests
{
    public class TrOcrEngineTests : BaseUnitTest
    {
        public override string BaseConfigSection { get; set; } = "TrOcr:ru";
        private TrOcrEngine _engine;

        [SetUp]
        public override void SetUp()
            => base.SetUp();
        
        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var engineOptions = ReadOptions();
            var preprocessor = new TrOcrPreprocessor(new TrOcrImageOptions());
            _engine = new TrOcrEngine(engineOptions, preprocessor);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown() 
        {
            _engine.Dispose();
        }

        [Test]
        [TestCase("TestImage", "Экономика и математика являются неотъемлемыми и взаимосвязанными", 20)]
        public void TrOcrEngine_ShouldRecognizeText(string fileName, string expectedString, int atLeastLength)
        {
            var imagePath = GetConfigValue(fileName);
            var stream = File.OpenRead(imagePath);

            var image = new LayoutImage { ImageStream = stream };
            var result = _engine.Recognize(image);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Not.Empty);
            Assert.That(result, Has.Length.AtLeast(atLeastLength));
            Assert.That(result, Is.EqualTo(expectedString));
        }

        private TrOcrEngineOptions ReadOptions()
        {
            var configPath = GetConfigValue("ModelConfigPath");
            var options = TrOcrModelConfigReader.Read(configPath);
            return options with
            {
                EncoderModelPath = GetConfigValue("EncoderModelPath"),
                DecoderModelPath = GetConfigValue("DecoderModelPath"),
                VocabPath = GetConfigValue("VocabPath")
            };
        }
    }
}
