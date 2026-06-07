using DiplomaMethod.Application.Extractors.Ocr.Recognizer.Base;
using DiplomaMethod.Core.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Recognizer;

public class OcrCustomRecognizer : IDisposable, IOcrRecognizer
{
    private readonly InferenceSession _session;
    private readonly OcrRecognizerOptions _options;
    private readonly List<string> _characterDictionary;

    public OcrCustomRecognizer(OcrRecognizerOptions options, SessionOptions? sessionOptions = null)
    {
        _options = options;

        if (sessionOptions == null)
        {
            sessionOptions = new SessionOptions()
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            sessionOptions.AppendExecutionProvider_CUDA();
            sessionOptions.AppendExecutionProvider_CPU();
        }

        _session = new InferenceSession(_options.ModelPath, sessionOptions);
        _characterDictionary = LoadDictionary(_options.DictionaryPath);
    }

    public async Task<string> RecognizeLineAsync(SKImage lineImage)
    {
        return await Task.Run(() =>
        {
            var tensor = PreprocessImage(lineImage);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("x", tensor)
            };

            using var outputs = _session.Run(inputs);
            return DecodeOutput(outputs);
        });
    }

    private DenseTensor<float> PreprocessImage(SKImage lineImage)
    {
        using var bitmap = SKBitmap.FromImage(lineImage);
        using var resized = ResizeImage(bitmap);

        var tensorData = new float[1 * 3 * _options.InputHeight * _options.InputWidth];
        var pixelData = resized.GetPixels();

        unsafe
        {
            byte* pixels = (byte*)pixelData.ToPointer();
            int pixelIndex = 0;

            for (int i = 0; i < _options.InputHeight * _options.InputWidth; i++)
            {
                byte r = pixels[i * 4 + 0];
                byte g = pixels[i * 4 + 1];
                byte b = pixels[i * 4 + 2];

                float rNorm = r / 255f;
                float gNorm = g / 255f;
                float bNorm = b / 255f;

                // NCHW format
                tensorData[0 * 3 * _options.InputHeight * _options.InputWidth + 0 * _options.InputHeight * _options.InputWidth + pixelIndex] = rNorm;
                tensorData[0 * 3 * _options.InputHeight * _options.InputWidth + 1 * _options.InputHeight * _options.InputWidth + pixelIndex] = gNorm;
                tensorData[0 * 3 * _options.InputHeight * _options.InputWidth + 2 * _options.InputHeight * _options.InputWidth + pixelIndex] = bNorm;

                pixelIndex++;
            }
        }

        return new DenseTensor<float>(tensorData, [1, 3, _options.InputHeight, _options.InputWidth]);
    }

    private SKBitmap ResizeImage(SKBitmap original)
    {
        var srcWidth = original.Width;
        var srcHeight = original.Height;
        var scale = Math.Min((double)_options.InputWidth / srcWidth, (double)_options.InputHeight / srcHeight);

        var newWidth = (int)(srcWidth * scale);
        var newHeight = (int)(srcHeight * scale);

        var resized = new SKBitmap(_options.InputWidth, _options.InputHeight);
        using var canvas = new SKCanvas(resized);

        canvas.Clear(SKColors.White);
        var srcRect = new SKRect(0, 0, srcWidth, srcHeight);
        var dstRect = new SKRect(0, 0, newWidth, newHeight);
        canvas.DrawBitmap(original, srcRect, dstRect);

        return resized;
    }

    private string DecodeOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var outputTensor = outputs[0].AsTensor<long>();
        var indices = outputTensor.ToArray();

        var result = new System.Text.StringBuilder();

        foreach (var index in indices)
        {
            if (index >= 0 && index < _characterDictionary.Count)
            {
                result.Append(_characterDictionary[(int)index]);
            }
        }

        return result.ToString().Trim();
    }

    private static List<string> LoadDictionary(string dictionaryPath)
    {
        var dictionary = new List<string>();

        if (!File.Exists(dictionaryPath))
            throw new FileNotFoundException($"Character dictionary not found: {dictionaryPath}");

        using var reader = new StreamReader(dictionaryPath);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                dictionary.Add(line.Trim());
        }

        return dictionary;
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
