using DiplomaMethod.Application.Extractors.Analyzers;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;
using DiplomaMethod.UnitTests.UtilsTests;

namespace DiplomaMethod.UnitTests.ExtractorsTests.AnalyzersTests;

[TestFixture]
public class DocstrumImageAnalyzerTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "TestData:Ocr";

    private string ResultsPath
    {
        get
        {
            var raw = Configuration["TestResults:Docstrum"] ?? "TestResults/Docstrum";
            if (Path.IsPathRooted(raw)) return raw;
            var asmDir = Path.GetDirectoryName(typeof(DocstrumImageAnalyzerTests).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            return Path.Combine(asmDir, raw);
        }
    }

    [Test]
    [TestCase("PageEN_1")]
    [TestCase("PageEN_2")]
    [TestCase("PageEN_Scan")]
    [TestCase("PageRU_HQ")]
    [TestCase("PageRU_LQ")]
    [TestCase("PageEN_MQ_NoHead")]
    [TestCase("PageEN_MQ_NoHead_2")]
    public async Task ClusterComponents_SavesVisualization(string configKey)
    {
        var imagePath = GetConfigValue(configKey);
        using var skImage = await OcrUtils.GetSKImageAsync(imagePath);

        var clusters = DocstrumImageAnalyzer.ClusterComponents(skImage);

        Console.WriteLine($"[{configKey}]  {skImage.Width}×{skImage.Height}px  → {clusters.Count} cluster(s)");
        foreach (var b in clusters)
            Console.WriteLine($"  ({b.X:F0},{b.Y:F0})  {b.Width:F0}×{b.Height:F0}");

        string saved = ImageTestUtils.SaveWithBoundingBoxes(skImage, clusters, ResultsPath, $"docstrum_{configKey}.png");
        Console.WriteLine($"  Saved → {saved}");

        Assert.That(clusters, Is.Not.Empty);
    }
}
