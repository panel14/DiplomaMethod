using DiplomaMethod.Core.Options;
using Florence2;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Fallback.Florence;

public class FlorenceEngine : IDisposable
{
    private readonly Florence2Model _model;
    private bool _disposed;

    private FlorenceEngine(Florence2Model model)
    {
        _model = model;
    }

    public static async Task<FlorenceEngine> CreateAsync(FlorenceOcrOptions options)
    {
        var modelSource = new FlorenceModelDownloader(options.ModelPath);
        if (options.AutoDownload)
            await modelSource.DownloadModelsAsync(_ => { }, NullLogger.Instance, CancellationToken.None);
        return new FlorenceEngine(new Florence2Model(modelSource));
    }

    public async Task<string> RecognizeLineAsync(SKImage lineImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(FlorenceEngine));

        return await Task.Run(() =>
        {
            using var data = lineImage.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return string.Empty;

            using var stream = data.AsStream();
            var results = _model.Run(TaskTypes.OCR, [stream], string.Empty, CancellationToken.None);
            return results.Length > 0 ? results[0].PureText?.Trim() ?? string.Empty : string.Empty;
        });
    }

    public async Task<string> RecognizeBlockAsync(Stream imageStream)
    {
        var results = _model.Run(TaskTypes.OCR, [imageStream], textInput: null, CancellationToken.None);
        return results.Length > 0 ? results[0].PureText?.Trim() ?? string.Empty : string.Empty; 
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
