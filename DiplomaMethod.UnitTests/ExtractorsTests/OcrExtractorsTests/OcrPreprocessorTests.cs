using DiplomaMethod.Application.Extractors.Ocr.Preprosessors;
using DiplomaMethod.UnitTests.Base;
using SkiaSharp;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests;

[TestFixture]
public class OcrPreprocessorTests : BaseUnitTest
{
    // Reuse the existing TestData:Ocr image paths
    public override string BaseConfigSection { get; set; } = "TestData:Ocr";

    private string ResultsPath =>
        Configuration["TestResults:OcrPreprocessor"] is { Length: > 0 } p
            ? (Path.IsPathRooted(p) ? p : Path.Combine(AssemblyDir, p))
            : Path.Combine(AssemblyDir, "TestResults", "OcrPreprocessor");

    private static string AssemblyDir =>
        Path.GetDirectoryName(typeof(OcrPreprocessorTests).Assembly.Location)!;

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    [TestCase("PageRU_HQ")]
    [TestCase("PageRU_LQ")]
    [TestCase("PageEN_Scan")]
    public void Process_SavesPreprocessedImage(string imageKey)
    {
        var opts = ReadOcrOptions().Preprocess;
        Assume.That(opts.Enabled, Is.True,
            "Preprocessing is disabled — set Ocr:Preprocess:Enabled=true in appsettings.local.json");

        var inputPath = GetConfigValue(imageKey);
        Assume.That(File.Exists(inputPath), Is.True, $"Image not found: '{inputPath}'");

        using var source = SKImage.FromEncodedData(inputPath);
        Assert.That(source, Is.Not.Null, $"Could not decode image: '{inputPath}'");

        Console.WriteLine($"Input:  {Path.GetFileName(inputPath)}  {source.Width}×{source.Height}px");

        var result = OcrPreprocessor.Process(source, opts);

        Assert.That(result, Is.Not.Null,
            "OcrPreprocessor.Process returned null — verify that Deskew or AdaptiveBinarize is enabled");

        using (result)
        {
            Console.WriteLine($"Output: {result.Width}×{result.Height}px");

            Directory.CreateDirectory(ResultsPath);
            var outFile = Path.Combine(ResultsPath, $"preprocessed_{imageKey}.png");

            using var data = result.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(outFile, data.ToArray());

            Console.WriteLine($"Saved → {outFile}");
            Assert.That(File.Exists(outFile));
        }
    }
}
