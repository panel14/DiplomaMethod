using DiplomaMethod.Application.Extractors.Ocr.Recognizer.Base;
using DiplomaMethod.Core.Options;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR.Models.Local;
using SkiaSharp;
using PaddleRecognizer = Sdcb.PaddleOCR.PaddleOcrRecognizer;

namespace DiplomaMethod.Application.Extractors.Ocr.Recognizers
{
    public class PaddleOcrRecognizer_V3 : IOcrRecognizer, IDisposable
    {
        private readonly PaddleRecognizer _recognizer;
        private bool _disposed;

        public PaddleOcrRecognizer_V3(PaddleOcrRecognizerV3Options? options = null)
        {
            options ??= new PaddleOcrRecognizerV3Options();
            var device = options.UseGpu
                ? PaddleDevice.Gpu(options.GpuId, options.GpuInitialMemoryMb)
                : PaddleDevice.Mkldnn();
            _recognizer = new PaddleRecognizer(LocalFullModels.CyrillicV3.RecognizationModel, device);
        }

        public async Task<string> RecognizeLineAsync(SKImage lineImage)
        {
            var (text, _) = await RecognizeWithScoreAsync(lineImage);
            return text;
        }

        public async Task<(string Text, float Score)> RecognizeWithScoreAsync(SKImage lineImage)
        {
            var results = await RecognizeBatchAsync([lineImage]);
            return results[0];
        }

        public async Task<IReadOnlyList<(string Text, float Score)>> RecognizeBatchAsync(
            IReadOnlyList<SKImage> lineImages)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(PaddleOcrRecognizer_V3));
            if (lineImages.Count == 0) return [];

            // Encode on the calling thread — SkiaSharp is not safe to call from Task.Run
            var byteBuffers = new byte[]?[lineImages.Count];
            for (int i = 0; i < lineImages.Count; i++)
            {
                using var data = lineImages[i]?.Encode(SKEncodedImageFormat.Png, 100);
                byteBuffers[i] = data?.ToArray();
            }

            return await Task.Run(() =>
            {
                var mats = new Mat[byteBuffers.Length];
                var validIdx = new List<int>(byteBuffers.Length);
                try
                {
                    for (int i = 0; i < byteBuffers.Length; i++)
                    {
                        var buf = byteBuffers[i];
                        Mat mat = buf is { Length: > 0 }
                            ? Mat.FromImageData(buf, ImreadModes.Color)
                            : new Mat();
                        mats[i] = mat;
                        if (!mat.Empty()) validIdx.Add(i);
                    }

                    var output = new (string Text, float Score)[byteBuffers.Length];
                    if (validIdx.Count > 0)
                    {
                        var validMats = validIdx.Select(i => mats[i]).ToArray();
                        var results = _recognizer.Run(validMats);
                        for (int j = 0; j < validIdx.Count; j++)
                            output[validIdx[j]] = (results[j].Text?.Trim() ?? string.Empty, results[j].Score);
                    }
                    return (IReadOnlyList<(string, float)>)output;
                }
                finally
                {
                    foreach (var mat in mats) mat?.Dispose();
                }
            });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _recognizer?.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
