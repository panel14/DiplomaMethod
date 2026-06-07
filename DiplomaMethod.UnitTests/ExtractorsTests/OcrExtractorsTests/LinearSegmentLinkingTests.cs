using DiplomaMethod.Application.Extractors.Ocr.Splitters.Line;
using DiplomaMethod.UnitTests.Base;
using DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base;
using SkiaSharp;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests;

/// <summary>
/// Unit tests for <see cref="LinearSegmentLinkingSplitter"/>.
/// Each test loads an image, runs the splitter, saves a visualization PNG with
/// detected line strips highlighted, and asserts that at least one line was found.
/// </summary>
[TestFixture]
public class LinearSegmentLinkingTests : BaseUnitTest
{
    public override string BaseConfigSection { get; set; } = "TestData:Ocr";

    private string ResultsPath
    {
        get
        {
            var raw = "TestResults/LinearSegmentLinking";
            var asmDir = Path.GetDirectoryName(typeof(LinearSegmentLinkingTests).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            return Path.Combine(asmDir, raw);
        }
    }

    private readonly LinearSegmentLinkingSplitter _splitter = new();

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Test]
    [TestCase("PageRU_HQ")]
    [TestCase("PageRU_LQ")]
    [TestCase("PageEN_1")]
    [TestCase("PageEN_2")]
    [TestCase("PageEN_Scan")]
    [TestCase("EnBlock")]
    [TestCase("RuBlockWaves")]
    public async Task Split_ReturnsLines_AndSavesVisualization(string configKey)
    {
        var imagePath = GetConfigValue(configKey);
        Assume.That(File.Exists(imagePath), Is.True, $"Test image not found: '{imagePath}'");

        using var source = await OcrUtils.GetSKImageAsync(imagePath);

        var lines = _splitter.Split(source);

        Console.WriteLine($"[{configKey}]  {source.Width}×{source.Height}px  → {lines.Count} line(s)");
        for (int i = 0; i < lines.Count; i++)
        {
            var r = lines[i];
            Console.WriteLine($"  [{i:D3}]  y=[{r.Top}, {r.Bottom})  h={r.Height}");
        }

        Directory.CreateDirectory(ResultsPath);
        string outFile = Path.Combine(ResultsPath, $"lsl_{configKey}.png");
        SaveVisualization(source, lines, outFile, configKey);
        Console.WriteLine($"  Saved → {outFile}");

        Assert.That(lines, Is.Not.Empty, $"LSL must detect at least one line in '{configKey}'");
    }

    // ─── Visualization helper ─────────────────────────────────────────────────

    /// <summary>
    /// Draws each detected line as a semi-transparent colored horizontal band
    /// over the original image and saves the result as a PNG.
    /// </summary>
    private static void SaveVisualization(
        SKImage source,
        IReadOnlyList<SKRectI> lines,
        string outputPath,
        string title)
    {
        const int headerH = 26;

        using var surface = SKSurface.Create(
            new SKImageInfo(source.Width, source.Height + headerH, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(240, 240, 240));

        // Header bar
        using var fillGray  = new SKPaint { Color = new SKColor(60, 60, 60),   Style = SKPaintStyle.Fill };
        using var textWhite = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font      = new SKFont  { Size = 13 };
        canvas.DrawRect(0, 0, source.Width, headerH, fillGray);
        canvas.DrawText($"LSL — {title}   ({lines.Count} lines)", 6, headerH - 6,
            SKTextAlign.Left, font, textWhite);

        // Original image shifted down by headerH
        canvas.DrawImage(source, 0, headerH);

        // Draw one semi-transparent band per line
        using var bandPaint = new SKPaint { Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false
        };

        for (int i = 0; i < lines.Count; i++)
        {
            var r     = lines[i];
            var color = BandColor(i);

            bandPaint.Color   = color.WithAlpha(50);
            borderPaint.Color = color.WithAlpha(200);

            float top    = r.Top    + headerH;
            float bottom = r.Bottom + headerH;

            canvas.DrawRect(0, top, source.Width, r.Height, bandPaint);
            canvas.DrawLine(0, top,    source.Width, top,    borderPaint);
            canvas.DrawLine(0, bottom, source.Width, bottom, borderPaint);

            // Line index label on the right edge
            canvas.DrawText($"{i}", source.Width - 28, top + r.Height / 2f + 4,
                SKTextAlign.Left, font, textWhite);
        }

        using var snapshot = surface.Snapshot();
        using var data     = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outputPath, data.ToArray());
    }

    private static SKColor BandColor(int idx)
    {
        uint[] palette =
        [
            0xFF2196F3, 0xFF4CAF50, 0xFFE91E63, 0xFF9C27B0,
            0xFFFFC107, 0xFF00BCD4, 0xFFFF5722, 0xFF607D8B,
        ];
        return new SKColor(palette[idx % palette.Length]);
    }
}
