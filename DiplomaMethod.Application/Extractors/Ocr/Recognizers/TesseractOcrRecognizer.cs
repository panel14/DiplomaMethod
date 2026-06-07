using DiplomaMethod.Application.Extractors.Ocr.Recognizer.Base;
using DiplomaMethod.Core.Options;
using SkiaSharp;
using Tesseract;

namespace DiplomaMethod.Application.Extractors.Ocr.Recognizers;

public class TesseractOcrRecognizer(TesseractOptions options) : IOcrRecognizer, IDisposable
{
    private readonly TesseractEngine _engine = new (options.TessDataPath, options.Language, EngineMode.Default);
    private readonly PageSegMode _pageSegMode = (PageSegMode)options.PageSegMode;
    private readonly int _upscaleMinHeight = options.UpscaleMinHeight;
    private readonly object _lock = new();
    private bool _disposed;

    public async Task<string> RecognizeLineAsync(SKImage lineImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(TesseractOcrRecognizer));

        return await Task.Run(() =>
        {
            using var prepared = PrepareImage(lineImage);
            using var data = prepared.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return string.Empty;

            var bytes = data.ToArray();

            lock (_lock)
            {
                using var pix = Pix.LoadFromMemory(bytes);
                using var page = _engine.Process(pix, _pageSegMode);
                return page.GetText()?.Trim() ?? string.Empty;
            }
        });
    }

    private SKImage PrepareImage(SKImage source)
    {
        if (source.Height >= _upscaleMinHeight)
            return source;

        var scale = (double)_upscaleMinHeight / source.Height;
        var newWidth = Math.Max(1, (int)(source.Width * scale));

        var info = new SKImageInfo(newWidth, _upscaleMinHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.DrawImage(source, new SKRect(0, 0, newWidth, _upscaleMinHeight));
        return surface.Snapshot();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _engine.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
