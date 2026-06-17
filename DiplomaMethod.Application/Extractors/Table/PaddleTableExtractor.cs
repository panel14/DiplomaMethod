using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using DiplomaMethod.Core.Services;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Table;

public class PaddleTableExtractor : IExtractor, IDisposable
{
    private readonly PaddleOcrTableRecognizer _tableRecognizer;
    private readonly PaddleOcrAll _ocr;
    private bool _disposed;

    public PaddleTableExtractor(PaddleOcrRecognizerV3Options options)
    {
        // CPU backend: mkldnn (oneDNN) on Windows, but OpenBLAS elsewhere. oneDNN fails a primitive
        // on some Linux/WSL CPUs ("dnnl::error: could not execute a primitive"), so on non-Windows we
        // use the OpenBLAS-built native (Sdcb.PaddleInference.runtime.linux-x64.openblas) instead.
        Action<PaddleConfig> device = options.UseGpu
            ? PaddleDevice.Gpu(options.GpuId, options.GpuInitialMemoryMb)
            : (OperatingSystem.IsWindows() ? PaddleDevice.Mkldnn() : PaddleDevice.Blas());

        _tableRecognizer = new PaddleOcrTableRecognizer(
            LocalTableRecognitionModel.ChineseMobileV2_SLANET, device);
        _ocr = new PaddleOcrAll(LocalFullModels.CyrillicV3, device);
    }

    public async Task<IReadOnlyList<TextBlock>> ReadAsync(
        LayoutImage image, IReadOnlyList<DetectionResult> detection)
    {
        var source = image.CachedImage;
        if (source == null) return [];

        var results = new List<TextBlock>();

        foreach (var det in detection)
        {
            using var tableImage = CropImage(source, det.BoundingBox);
            if (tableImage == null) continue;

            using var encoded = tableImage.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded == null) continue;

            var bytes = encoded.ToArray();

            var (html, confidence) = await Task.Run<(string? Html, float Score)>(() =>
            {
                using var mat = Mat.FromImageData(bytes, ImreadModes.Color);
                if (mat.Empty()) return (null, 0f);

                var tableResult = _tableRecognizer.Run(mat);
                var ocrResult = _ocr.Run(mat);
                return (tableResult.RebuildTable(ocrResult), tableResult.Score);
            });

            if (string.IsNullOrWhiteSpace(html)) continue;

            results.Add(new TextBlock
            {
                Lines = [new TextLine { Text = html, Box = det.BoundingBox }],
                Box = det.BoundingBox,
                Label = det.Label,
                Confidence = confidence
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _tableRecognizer.Dispose();
            _ocr.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
