using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using DiplomaMethod.Core.Services;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace DiplomaMethod.Application.Classifiers;

public readonly record struct LetterboxInfo(float Scale, float PadX, float PadY, int OriginalWidth, int OriginalHeight);

public abstract class BaseOnnxClassifier : ILayoutClassifier, IDisposable
{
    protected InferenceSession _session;
    protected ClassifierOptions _options;

    protected BaseOnnxClassifier(string modelPath, SessionOptions? options = null,
        ClassifierOptions? classifierOptions = null)
    {
        if (options == null)
        {
            options = new SessionOptions()
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            // Use CUDA only when the runtime actually exposes the provider, and never let a missing /
            // mismatched CUDA stack abort startup — fall back to CPU. On a CPU-only or non-GPU
            // onnxruntime native the CUDA entry point is absent (EntryPointNotFoundException), so the
            // append is both availability-checked and wrapped.
            if (OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider"))
            {
                try
                {
                    // Provider-options API (libonnxruntime_providers_shared/_cuda) instead of the legacy
                    // entry point, which the Linux GPU build does not export → EntryPointNotFoundException.
                    using var cudaOptions = new OrtCUDAProviderOptions();
                    options.AppendExecutionProvider_CUDA(cudaOptions);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"CUDA provider unavailable, using CPU: {ex.Message}");
                }
            }
            options.AppendExecutionProvider_CPU();
        }

        _session = new InferenceSession(modelPath, options);
        _options = classifierOptions ?? new ClassifierOptions();
    }

    public abstract Task<IEnumerable<DetectionResult>> ClassifyAsync(LayoutImage image);

    protected float[] ConvertToArray(LayoutImage image)
    {
        SKImage? ownedImage = null;
        SKImage skImage;

        if (image.CachedImage != null)
        {
            skImage = image.CachedImage;
        }
        else
        {
            image.ImageStream.Seek(0, SeekOrigin.Begin);
            ownedImage = SKImage.FromEncodedData(image.ImageStream)
                ?? throw new InvalidOperationException("Failed to decode image from stream");
            skImage = ownedImage;
        }

        using var bitmap = SKBitmap.FromImage(skImage);
        ownedImage?.Dispose();

        using var scaledBitmap = new SKBitmap(_options.TargetWidth, _options.TargetHeight);
        using var canvas = new SKCanvas(scaledBitmap);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(bitmap,
            new SKRect(0, 0, bitmap.Width, bitmap.Height),
            new SKRect(0, 0, _options.TargetWidth, _options.TargetHeight));

        using var rgbaBitmap = new SKBitmap(new SKImageInfo(_options.TargetWidth, _options.TargetHeight, SKColorType.Rgb888x));
        scaledBitmap.CopyTo(rgbaBitmap, SKColorType.Rgba8888);

        int hw = _options.TargetHeight * _options.TargetWidth;
        float[] tensorData = new float[_options.ChannelCount * hw];

        IntPtr pixelPtr = rgbaBitmap.GetPixels();
        unsafe
        {
            byte* pixels = (byte*)pixelPtr.ToPointer();
            for (int i = 0; i < hw; i++)
            {
                tensorData[0 * hw + i] = pixels[i * 4 + 0] / 255f;
                tensorData[1 * hw + i] = pixels[i * 4 + 1] / 255f;
                tensorData[2 * hw + i] = pixels[i * 4 + 2] / 255f;
            }
        }

        return tensorData;
    }
    
    protected (float[] Tensor, LetterboxInfo Letterbox) ConvertToLetterboxArray(LayoutImage image)
    {
        SKImage? ownedImage = null;
        SKImage skImage;

        if (image.CachedImage != null)
        {
            skImage = image.CachedImage;
        }
        else
        {
            image.ImageStream.Seek(0, SeekOrigin.Begin);
            ownedImage = SKImage.FromEncodedData(image.ImageStream)
                ?? throw new InvalidOperationException("Failed to decode image from stream");
            skImage = ownedImage;
        }

        using var bitmap = SKBitmap.FromImage(skImage);
        ownedImage?.Dispose();

        int origW = bitmap.Width;
        int origH = bitmap.Height;
        float scale = Math.Min((float)_options.TargetWidth / origW, (float)_options.TargetHeight / origH);
        int scaledW = (int)(origW * scale);
        int scaledH = (int)(origH * scale);
        float padX = (_options.TargetWidth  - scaledW) / 2f;
        float padY = (_options.TargetHeight - scaledH) / 2f;

        using var letterboxBitmap = new SKBitmap(_options.TargetWidth, _options.TargetHeight);
        using var canvas = new SKCanvas(letterboxBitmap);
        canvas.Clear(new SKColor(114, 114, 114));
        canvas.DrawBitmap(bitmap,
            new SKRect(0, 0, origW, origH),
            new SKRect(padX, padY, padX + scaledW, padY + scaledH));

        using var rgbaBitmap = new SKBitmap(new SKImageInfo(_options.TargetWidth, _options.TargetHeight, SKColorType.Rgb888x));
        letterboxBitmap.CopyTo(rgbaBitmap, SKColorType.Rgba8888);

        int hw = _options.TargetHeight * _options.TargetWidth;
        float[] tensorData = new float[_options.ChannelCount * hw];

        IntPtr pixelPtr = rgbaBitmap.GetPixels();
        unsafe
        {
            byte* pixels = (byte*)pixelPtr.ToPointer();
            for (int i = 0; i < hw; i++)
            {
                tensorData[0 * hw + i] = pixels[i * 4 + 0] / 255f;
                tensorData[1 * hw + i] = pixels[i * 4 + 1] / 255f;
                tensorData[2 * hw + i] = pixels[i * 4 + 2] / 255f;
            }
        }

        return (tensorData, new LetterboxInfo(scale, padX, padY, origW, origH));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session?.Dispose();
        }
    }
}
