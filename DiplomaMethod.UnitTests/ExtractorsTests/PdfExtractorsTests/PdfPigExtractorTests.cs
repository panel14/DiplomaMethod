using DiplomaMethod.Application.Extractors.Direct;
using Moq;
using UglyToad.PdfPig.Content;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.PdfExtractorsTests.Utils;
using DiplomaMethod.Application.Extractors.Analyzers;

namespace DiplomaMethod.UnitTests.ExtractorsTests.PdfExtractorsTests;

[TestFixture]
public class PdfPigExtractorTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "";
    private PdfPigExtractorService _service;

    [SetUp]
    public void Setup()
    {
        var validator = new Mock<ITextValidator>();
        validator.Setup(v => v.ValidateStrongText(It.IsAny<List<Word>>())).Returns(true);
        validator.Setup(v => v.EvaluateBlock(It.IsAny<TextBlock>())).Returns([]);

        _service = new PdfPigExtractorService(validator.Object);
    }

    [Test]
    [TestCase("Documents/document.pdf")]
    public async Task ReadAsync_ShouldExtractTextBlocks(string fileName)
    {
        var image = PdfExtractorsUtils.CreateTestLayoutImage(fileName, 1);
        var detection = new List<DetectionResult>
        {
            new() {
                Label = "TestLabel",
                Confidence = 0.9,
                BoundingBox = new BoundingBox { X = 40, Y = 300, Width = 710, Height = 700 }
            }
        };

        var result = await _service.ReadAsync(image, detection);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Not.Empty);
    }

    [Test]
    [TestCase("Documents/document.pdf", 257)]
    public void ExtractWord_ShouldExtractWordsFromArea(string fileName, int expectedWordsCount)
    {
        using var image = PdfExtractorsUtils.CreateTestLayoutImage(fileName, 1);
        var detection = new DetectionResult
        {
            BoundingBox = new BoundingBox { X = 40, Y = 300, Width = 270, Height = 350 }
        };

        var words = PdfPigExtractorService.ExtractAllWords(image.ImageStream, image.Page.PageNumber);
        var areaWords = PdfPigExtractorService.ExtractWordsFromArea(detection, words);

        Assert.That(areaWords, Is.Not.Empty);
        Assert.That(areaWords, Has.Count.EqualTo(expectedWordsCount));
    }

    [Test]
    [TestCase("Documents/document.pdf", 30)]
    public void BuildLines_ShouldBuildLinesCorrect(string fileName, int expectedLinesCount)
    {
        using var image = PdfExtractorsUtils.CreateTestLayoutImage(fileName, 1);
        var detection = new DetectionResult
        {
            BoundingBox = new BoundingBox { X = 40, Y = 300, Width = 270, Height = 350 }
        };

        var words = PdfPigExtractorService.ExtractAllWords(image.ImageStream, 1);
        var areaWords = PdfPigExtractorService.ExtractWordsFromArea(detection, words);
        var textBlock = PdfPigExtractorService.BuildLines(areaWords, "Text");

        Assert.That(textBlock, Is.Not.Null);
        Assert.That(textBlock.Lines, Has.Count.EqualTo(expectedLinesCount));
    }

    [Test]
    [TestCase("Documents/document.pdf", 4)]
    public void SplitByIndent_ShouldSplitByParagraphs(string fileName, int expectedParagraphsCount)
    {
        using var image = PdfExtractorsUtils.CreateTestLayoutImage(fileName, 1);
        var detection = new DetectionResult
        {
            BoundingBox = new BoundingBox { X = 40, Y = 300, Width = 270, Height = 350 }
        };

        var words = PdfPigExtractorService.ExtractAllWords(image.ImageStream, 1);
        var areaWords = PdfPigExtractorService.ExtractWordsFromArea(detection, words);
        var textBlock = PdfPigExtractorService.BuildLines(areaWords, "Text");
        var paragraphs = PdfPigExtractorService.SplitByLineIndent(textBlock, "Text");

        Assert.That(paragraphs, Has.Count.EqualTo(expectedParagraphsCount));
    }

    [Test]
    [TestCase("Documents/document.pdf")]
    public void DocstrumAnalyzer_ShouldFindConnections(string fileName)
    {
        using var image = PdfExtractorsUtils.CreateTestLayoutImage(fileName, 1);
        var detection = new DetectionResult
        {
            BoundingBox = new BoundingBox { X = 40, Y = 225, Width = 710, Height = 700 }
        };
        var words = PdfPigExtractorService.ExtractAllWords(image.ImageStream, 1);

        var blocks = DocstrumPdfAnalyzer.ClusterWords(words);
        Assert.That(blocks, Is.Not.Null);
        Assert.That(blocks, Has.Count.AtLeast(1));
    }
}
