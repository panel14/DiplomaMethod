using DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;
using SkiaSharp;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests;

/// <summary>
/// Unit tests for <see cref="AdaptiveOverSplitMergeSplitter"/>.
/// For each line image the test runs the splitter, saves a visualization PNG with
/// green word bounding boxes and red vertical dividers, and logs results to console.
/// </summary>
[TestFixture]
public class AdaptiveOverSplitMergeTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "TestData:Ocr";

    private string ResultsPath
    {
        get
        {
            var raw    = "TestResults/AdaptiveOverSplitMerge";
            var asmDir = Path.GetDirectoryName(typeof(AdaptiveOverSplitMergeTests).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            return Path.Combine(asmDir, raw);
        }
    }

    private readonly AdaptiveOverSplitMergeSplitter _splitter = new();

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    [TestCase("LineRU_1")]
    [TestCase("LineRU_2")]
    [TestCase("LineRU_3")]
    [TestCase("LineRU_4")]
    [TestCase("LineRU_5")]
    [TestCase("LineRU_6")]
    [TestCase("LineRU_7")]
    [TestCase("LineRU_8")]
    [TestCase("LineRU_9")]
    [TestCase("LineRU_10")]
    [TestCase("LineEN_1")]
    [TestCase("LineEN_2")]
    [TestCase("LineEN_3")]
    public async Task Split_DetectsWords_AndSavesVisualization(string configKey)
    {
        var imagePath = GetConfigValue(configKey);
        Assume.That(File.Exists(imagePath), Is.True, $"Test image not found: '{imagePath}'");

        using var source = await OcrUtils.GetSKImageAsync(imagePath);
        var words = _splitter.Split(source);

        Console.WriteLine($"[{configKey}]  {source.Width}×{source.Height}px  →  {words.Count} word(s)");
        for (int i = 0; i < words.Count; i++)
        {
            var r = words[i];
            Console.WriteLine($"  [{i}]  x=[{r.Left}, {r.Right})  w={r.Width}");
        }

        Directory.CreateDirectory(ResultsPath);
        string outFile = Path.Combine(ResultsPath, $"aom_{configKey}.png");
        SaveVisualization(source, words, outFile, configKey);
        Console.WriteLine($"  Saved → {outFile}");

        Assert.Pass($"Completed: {words.Count} word(s) found in '{configKey}'");
    }

    // ─── Visualization helper ─────────────────────────────────────────────────

    /// <summary>
    /// Draws green word bounding boxes and red vertical dividers at each
    /// word boundary over a 3× upscaled copy of the line image.
    /// The 3× scale makes individual column cuts clearly visible.
    /// </summary>
    private static void SaveVisualization(
        SKImage source,
        IReadOnlyList<SKRectI> words,
        string outputPath,
        string title)
    {
        const int Scale   = 3;
        const int HeaderH = 24;
        const int PadV    = 8;

        int sw = source.Width  * Scale;
        int sh = source.Height * Scale;
        int totalH = HeaderH + PadV + sh + PadV;

        using var surface = SKSurface.Create(
            new SKImageInfo(sw, totalH, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(200, 200, 200));

        // Header
        using var fillDark  = new SKPaint { Color = new SKColor(45, 45, 45),  Style = SKPaintStyle.Fill };
        using var textWhite = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font      = new SKFont  { Size = 13 };
        canvas.DrawRect(0, 0, sw, HeaderH, fillDark);
        canvas.DrawText($"AOM — {title}   ({words.Count} word(s))", 6, HeaderH - 6,
            SKTextAlign.Left, font, textWhite);

        // White background + upscaled line image
        int imgTop = HeaderH + PadV;
        using var fillWhite = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        canvas.DrawRect(0, imgTop, sw, sh, fillWhite);
        canvas.DrawImage(source, new SKRect(0, imgTop, sw, imgTop + sh));

        // Green word bounding boxes
        using var wordBoxPaint = new SKPaint
        {
            Color       = new SKColor(0, 200, 60),
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = false,
        };
        foreach (var r in words)
            canvas.DrawRect(r.Left * Scale, imgTop, r.Width * Scale, sh, wordBoxPaint);

        // Red vertical dividers between consecutive words
        using var dividerPaint = new SKPaint
        {
            Color       = new SKColor(220, 40, 40),
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false,
        };
        for (int i = 1; i < words.Count; i++)
        {
            float xDiv = words[i].Left * Scale - 0.5f;
            canvas.DrawLine(xDiv, imgTop, xDiv, imgTop + sh, dividerPaint);
        }

        using var snapshot = surface.Snapshot();
        using var data     = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outputPath, data.ToArray());
    }
}
