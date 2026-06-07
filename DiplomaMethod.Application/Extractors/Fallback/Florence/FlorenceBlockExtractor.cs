using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Services;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Fallback.Florence;

public class FlorenceBlockExtractor(FlorenceEngine engine) : IExtractor
{
    public async Task<IReadOnlyList<TextBlock>> ReadAsync(LayoutImage image, IReadOnlyList<DetectionResult> detection)
    {
        var source = image.CachedImage;
        if (source == null) return [];

        var results = new List<TextBlock>();

        foreach (var det in detection)
        {
            using var blockImage = CropImage(source, det.BoundingBox);
            if (blockImage == null) continue;

            using var encoded = blockImage.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded == null) continue;

            using var stream = encoded.AsStream();
            var text = await Task.Run(() => engine.RecognizeBlockAsync(stream));
            if (string.IsNullOrWhiteSpace(text)) continue;

            var lines = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0)
                .Select(l => new TextLine { Text = l, Box = det.BoundingBox })
                .ToList();

            if (lines.Count > 0)
                results.Add(new TextBlock
                {
                    Lines = lines,
                    Box = det.BoundingBox,
                    Label = string.IsNullOrEmpty(det.Label) ? "Text" : det.Label
                });
        }

        return results;
    }

    private static SKImage? CropImage(SKImage source, BoundingBox bbox)
    {
        var x = Math.Max(0, (int)bbox.X);
        var y = Math.Max(0, (int)bbox.Y);
        var w = Math.Min((int)bbox.Width, source.Width - x);
        var h = Math.Min((int)bbox.Height, source.Height - y);
        if (w <= 0 || h <= 0) return null;
        return source.Subset(new SKRectI(x, y, x + w, y + h));
    }
}
