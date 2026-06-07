using SkiaSharp;

namespace DiplomaMethod.UnitTests.ExtractorsTests.OcrExtractorsTests.Base
{
    public static class OcrUtils
    {
        public static async Task<SKImage> GetSKImageAsync(string path)
        {
            var imageData = await File.ReadAllBytesAsync(path);

            var bitmap = SKBitmap.Decode(imageData) ?? throw new NotSupportedException($"Unable to decode image at '{path}'");
            var scaledBitmap = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(scaledBitmap);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);

            return SKImage.FromBitmap(scaledBitmap);
        }
    }
}
