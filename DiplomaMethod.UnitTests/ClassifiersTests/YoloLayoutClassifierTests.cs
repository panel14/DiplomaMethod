using DiplomaMethod.Application.Classifiers;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.UtilsTests;
using SkiaSharp;

namespace DiplomaMethod.UnitTests.ClassifiersTests;

public class YoloLayoutClassifierTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "YoloClassifier";

    private string ModelPath      => GetConfigValue("ModelPath");
    private string TestImagePath  => GetConfigValue("TestImagePath");
    private string TestResultsPath => GetConfigValue("TestResultsPath");

    // Prints tensor shape, actual class count, and per-class detection summary.
    // Run this first to figure out the correct label mapping.
    [Test]
    public async Task ClassifyAsync_PrintClassDiagnostics()
    {
        using var classifier = new YoloLayoutClassifier(ModelPath);
        var image = LoadLayoutImage(TestImagePath);

        var detections = (await classifier.ClassifyAsync(image)).ToList();

        var groups = detections
            .GroupBy(d => d.ClassIndex)
            .OrderBy(g => g.Key);

        Console.WriteLine($"Total detections: {detections.Count}");
        Console.WriteLine($"Class distribution (raw index → current label → count):");
        foreach (var g in groups)
        {
            string label = g.First().Label;
            double avgConf = g.Average(d => d.Confidence);
            Console.WriteLine($"  idx={g.Key,3}  label={label,-20}  count={g.Count(),3}  avgConf={avgConf:P0}");
        }

        Assert.That(detections, Is.Not.Empty);
    }

    [Test]
    public async Task ClassifyAsync_OnDocumentImage_ReturnsDetections()
    {
        using var classifier = new YoloLayoutClassifier(ModelPath);
        var image = LoadLayoutImage(TestImagePath);

        var detections = (await classifier.ClassifyAsync(image)).ToList();

        Console.WriteLine($"Detected {detections.Count} regions:");
        foreach (var d in detections)
            Console.WriteLine($"  [{d.ClassIndex,2}] {d.Label,-20} conf={d.Confidence:F2}  " +
                              $"box=({d.BoundingBox.X:F0},{d.BoundingBox.Y:F0} " +
                              $"{d.BoundingBox.Width:F0}x{d.BoundingBox.Height:F0})");

        Assert.That(detections, Is.Not.Empty, "Expected at least one detection");
    }

    // Saves image with raw class indices as labels so the correct mapping can be established.
    [Test]
    public async Task ClassifyAsync_OnDocumentImage_SavesVisualizationWithRawIndices()
    {
        using var classifier = new YoloLayoutClassifier(ModelPath);
        var image = LoadLayoutImage(TestImagePath);

        var detections = (await classifier.ClassifyAsync(image)).ToList();

        // Use raw index as label text so we can see which index = which visual element
        var rawDetections = detections
            .Select(d => d with { Label = $"[{d.ClassIndex}] {d.Label}" })
            .ToList();

        string outputPath = ImageTestUtils.SaveWithDetections(image.CachedImage!, rawDetections, TestResultsPath, "detections_raw.png");
        Console.WriteLine($"Detected {detections.Count} regions. Saved to: {outputPath}");
        Assert.That(File.Exists(outputPath), Is.True);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static LayoutImage LoadLayoutImage(string path)
    {
        using var stream = File.OpenRead(path);
        var image = SKImage.FromEncodedData(stream)
            ?? throw new InvalidOperationException($"Cannot decode image: {path}");

        return new LayoutImage
        {
            CachedImage = image,
            ImageStream = Stream.Null,
            Page = new PageInfo { DocumentId = "test", PageId = "page_1", PageNumber = 1 }
        };
    }

}
