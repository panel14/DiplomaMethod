using DiplomaMethod.Core.Models.LayoutClassification;
using SkiaSharp;

namespace DiplomaMethod.UnitTests.UtilsTests;

public static class ImageTestUtils
{
    public static string SaveWithDetections(
        SKImage source,
        IReadOnlyList<DetectionResult> detections,
        string outputDir,
        string fileName)
    {
        var annotations = detections
            .Select(d => (d.BoundingBox, $"{d.Label} {d.Confidence:P0}", LabelColor(d.Label, d.ClassIndex)))
            .ToList();
        return SaveAnnotated(source, annotations, outputDir, fileName);
    }

    public static string SaveWithBoundingBoxes(
        SKImage source,
        IReadOnlyList<BoundingBox> boxes,
        string outputDir,
        string fileName)
    {
        var annotations = boxes
            .Select((b, i) => (b, $"[{i}]", ColorFromIndex(i)))
            .ToList();
        return SaveAnnotated(source, annotations, outputDir, fileName);
    }

    private static string SaveAnnotated(
        SKImage source,
        IReadOnlyList<(BoundingBox Box, string Label, SKColor Color)> annotations,
        string outputDir,
        string fileName)
    {
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, fileName);

        using var surface = SKSurface.Create(
            new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.DrawImage(source, 0, 0);

        using var font      = new SKFont { Size = 14 };
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var boxPaint  = new SKPaint { IsAntialias = false };

        foreach (var (box, label, color) in annotations)
        {
            float x = (float)box.X, y = (float)box.Y;
            float w = (float)box.Width, h = (float)box.Height;

            boxPaint.Color       = color.WithAlpha(180);
            boxPaint.Style       = SKPaintStyle.Stroke;
            boxPaint.StrokeWidth = 2;
            canvas.DrawRect(x, y, w, h, boxPaint);

            float textW = font.MeasureText(label);
            float textH = font.Size + 4;
            boxPaint.Style = SKPaintStyle.Fill;
            boxPaint.Color = color.WithAlpha(200);
            canvas.DrawRect(x, y - textH, textW + 6, textH, boxPaint);
            canvas.DrawText(label, x + 3, y - 3, SKTextAlign.Left, font, textPaint);
        }

        using var img  = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs   = File.OpenWrite(outputPath);
        data.SaveTo(fs);
        return outputPath;
    }

    private static SKColor LabelColor(string label, int classIndex)
    {
        var map = BuildColorMap();
        return map.TryGetValue(label, out var c) ? c : ColorFromIndex(classIndex);
    }

    public static SKColor ColorFromIndex(int idx)
    {
        uint[] palette =
        [
            0xFF2196F3, 0xFFE91E63, 0xFF4CAF50, 0xFF9C27B0, 0xFFFFC107,
            0xFF00BCD4, 0xFFFF5722, 0xFF607D8B, 0xFF795548, 0xFF009688,
            0xFF3F51B5, 0xFFF44336
        ];
        return new SKColor(palette[idx % palette.Length]);
    }

    public static Dictionary<string, SKColor> BuildColorMap() => new()
    {
        ["Text"]           = SKColors.DodgerBlue,
        ["Title"]          = SKColors.OrangeRed,
        ["List"]           = SKColors.MediumSeaGreen,
        ["Table"]          = SKColors.MediumOrchid,
        ["Figure"]         = SKColors.Goldenrod,
        ["Figure Caption"] = SKColors.DarkOrange,
        ["Page Header"]    = SKColors.SlateBlue,
        ["Page Footer"]    = SKColors.SlateBlue,
        ["Page Number"]    = SKColors.Gray,
        ["Footnote"]       = SKColors.RosyBrown,
        ["Annotation"]     = SKColors.Teal,
    };
}
