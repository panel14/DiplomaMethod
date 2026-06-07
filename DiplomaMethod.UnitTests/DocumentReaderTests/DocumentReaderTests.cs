using DiplomaMethod.Application.DocumentReader;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.UnitTests.Base;

namespace DiplomaMethod.UnitTests.DocumentReaderTests;

[TestFixture]
public class DocumentReaderTests : BaseUnitTest
{
    private DocumentReader _reader;

    public override string BaseConfigSection { get; set; } = "TestData/Documents";

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _reader = new DocumentReader();
    }

    [TearDown]
    public void TearDown()
    {
    }

    [Test]
    [TestCase("SamplePdfPath")]
    public async Task DocumentReader_ShouldReadPdfPage(string path)
    {
        // Arrange
        var pdfPath = GetConfigValue(path);
        var images = new List<LayoutImage?>();

        // Act
        await foreach (var image in _reader.ReadDocumentAsync(pdfPath))
        {
            images.Add(image);
        }

        //Assert
        Assert.That(images, Is.Not.Empty, "PDF should contain at least one page");
        
        var firstImage = images[0];
        Assert.That(firstImage, Is.Not.Null, "First page image should not be null");

        Assert.That(firstImage.Page, Is.Not.Null, "Page info should be populated");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstImage.Page.DocumentId, Is.Not.Empty, "DocumentId should be set");
            Assert.That(firstImage.Page.PageNumber, Is.GreaterThan(0), "PageNumber should be > 0");

            Assert.That(firstImage.CachedImage, Is.Not.Null, "CachedImage should contain rendered page");
        }

    }

    [TestCase("SampleImagePath")]
    public async Task DocumentReader_ShouldReadImage(string path)
    {
        // Arrange
        var imagePath = GetConfigValue(path);
        var images = new List<LayoutImage>();

        // Act
        await foreach (var image in _reader.ReadDocumentAsync(imagePath))
        {
            images.Add(image);
        }

        // Assert
        Assert.That(images, Is.Not.Empty, "Image should be readable");
        Assert.That(images, Has.Count.EqualTo(1), "Single image should return exactly one page");
        
        var layoutImage = images[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(layoutImage.Page.PageNumber, Is.EqualTo(1), "Image should be page 1");
            Assert.That(layoutImage.CachedImage, Is.Not.Null, "Image data should be cached");
        }
    }

    [Test]
    public void DocumentReader_ShouldThrow_WhenFileNotFound()
    {
        var ex = Assert.ThrowsAsync<FileNotFoundException>(
            async () => {await foreach (var _ in _reader.ReadDocumentAsync("no/path")) { }}
        );
        
        Assert.That(ex.Message, Contains.Substring("not found"), "Error message should mention file not found");
    }

    [TestCase("SampleUsupportedPath")]
    public void DocumentReader_ShouldThrow_WhenUnsupportedFormat(string path)
    {
        var unsuportedPath = GetConfigValue(path);
        var ex = Assert.ThrowsAsync<NotSupportedException>(
            async () => {await foreach (var _ in _reader.ReadDocumentAsync(unsuportedPath)) { }}
        );
        
        Assert.That(ex.Message, Contains.Substring("not supported"), "Error should mention unsupported format");
    }

    [TestCase("SamplePdfPath")]
    public async Task DocumentReader_ShouldPreservePageMetadata(string path)
    {
        // Arrange
        var imagePath = GetConfigValue(path);

        // Act
        var layouts = new List<LayoutImage>();
        await foreach (var image in _reader.ReadDocumentAsync(imagePath))
        {
            layouts.Add(image);
        }

        var layout = layouts[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(layout.Page.DocumentId, Is.EqualTo(Path.GetFileNameWithoutExtension(imagePath)),
                    "DocumentId should match filename");
            Assert.That(layout.Page.PageId, Is.Not.Empty, "PageId should be set");
        }

    }
}
