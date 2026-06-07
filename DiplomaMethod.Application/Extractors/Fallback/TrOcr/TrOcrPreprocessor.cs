using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Options.TrOcr;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Fallback.TrOcr
{
    public class TrOcrPreprocessor(TrOcrImageOptions options)
    {
        private readonly TrOcrImageOptions _options = options;

        public float[] PreprocessImage(LayoutImage image)
        {
            image.ImageStream.Seek(0, SeekOrigin.Begin);

            using var skImage = SKImage.FromEncodedData(image.ImageStream);
            using var bitmap = SKBitmap.FromImage(skImage);

            using var scaledBitmap = new SKBitmap(_options.TargetWidth, _options.TargetHeight);
            using var canvas = new SKCanvas(scaledBitmap);

            canvas.Clear(SKColors.White);
            var srcRect = new SKRect(0, 0, bitmap.Width, bitmap.Height);
            var dstRect = new SKRect(0, 0, _options.TargetWidth, _options.TargetHeight);
            canvas.DrawBitmap(bitmap, srcRect, dstRect);

            using var rgbaBitmap = new SKBitmap(new SKImageInfo(_options.TargetWidth, _options.TargetHeight, SKColorType.Rgb888x));
            scaledBitmap.CopyTo(rgbaBitmap, SKColorType.Rgb888x);

            var tensor = new float[1 * 3 * _options.TargetWidth * _options.TargetHeight];
            unsafe
            {
                byte* ptr = (byte*)rgbaBitmap.GetPixels().ToPointer();
                int bytesPerPixel = rgbaBitmap.BytesPerPixel;

                for (int y = 0; y < 384; y++)
                {
                    for (int x = 0; x < 384; x++)
                    {
                        int offset = (y * 384 + x) * bytesPerPixel;
                        float b = ptr[offset] / 255f;
                        float g = ptr[offset + 1] / 255f;
                        float r = ptr[offset + 2] / 255f;

                        int idx = y * 384 + x;
                        tensor[0 * 384 * 384 + idx] = (r - _options.Mean[0]) / _options.Std[0];
                        tensor[1 * 384 * 384 + idx] = (g - _options.Mean[1]) / _options.Std[1];
                        tensor[2 * 384 * 384 + idx] = (b - _options.Mean[2]) / _options.Std[2];
                    }
                }
                return tensor;
            }
        }
    }
}
