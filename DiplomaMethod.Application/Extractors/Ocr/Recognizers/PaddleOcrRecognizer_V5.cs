using System.Diagnostics;
using System.Text;
using DiplomaMethod.Application.Diagnostics;
using DiplomaMethod.Application.Extractors.Ocr.Recognizer.Base;
using DiplomaMethod.Core.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Recognizers;

/// <summary>
/// PP-OCRv5 recognition model loaded from a pre-exported ONNX file.
///
/// Pipeline:
///   1. Resize each line image to height=48, variable width (aspect-preserving).
///   2. Normalize pixels: (value/255 − 0.5) / 0.5  →  range [−1, 1].
///   3. Assemble NCHW batch tensor [B, 3, 48, maxW], padding shorter images with white.
///   4. Run ONNX inference → CTC probabilities [B, T, num_classes] (the export already applies softmax).
///   5. Greedy CTC decode: argmax per timestep, collapse repeats, drop blank (index 0).
///   6. Return text + mean confidence at selected positions. The model output values ARE probabilities,
///      so confidence is read directly (as in PaddleOCR's preds_prob.max(axis=2).mean()); applying
///      softmax again would collapse every score to ~1/num_classes.
///
/// Compatible with models from https://huggingface.co/monkt/paddleocr-onnx
/// (languages/eslav and languages/english).
/// </summary>
public sealed class PaddleOcrRecognizer_V5(PaddleOcrRecognizerV5Options options) : IOcrRecognizer, IDisposable
{
    private const int TargetHeight = 48;

    private readonly InferenceSession _session = CreateSession(options);
    private readonly IReadOnlyList<string> _chars = LoadDict(options.DictPath, options.UseSpaceChar);
    private readonly PaddleOcrRecognizerV5Options _options = options;
    private bool _disposed;

    // ── Public API ────────────────────────────────────────────────────────────


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
        ObjectDisposedException.ThrowIf(_disposed, nameof(PaddleOcrRecognizer_V5));
        if (lineImages.Count == 0) return [];

        // SkiaSharp pixel access must stay on the calling thread.
        long t0 = Stopwatch.GetTimestamp();
        var (batchData, widths, maxWidth) = BuildBatchTensor(lineImages);
        if (OcrProfiler.Enabled) OcrProfiler.AddBatchBuild(Stopwatch.GetTimestamp() - t0);

        if (maxWidth == 0)
            return new (string, float)[lineImages.Count];

        if (OcrProfiler.Enabled)
        {
            long widthSum = 0;
            foreach (var w in widths) widthSum += w;
            OcrProfiler.AddPaddleBatch(lineImages.Count, widthSum);
        }

        return await Task.Run(() => Infer(batchData, lineImages.Count, maxWidth, widths));
    }

    // ── Preprocessing ─────────────────────────────────────────────────────────

    private (float[] BatchData, int[] Widths, int MaxWidth) BuildBatchTensor(
        IReadOnlyList<SKImage> images)
    {
        int n        = images.Count;
        var widths   = new int[n];
        var tensors  = new float[n][];
        int maxWidth = 0;

        for (int i = 0; i < n; i++)
        {
            var img = images[i];
            if (img is null || img.Width == 0 || img.Height == 0)
            {
                tensors[i] = [];
                continue;
            }

            int w = Math.Max(1, (int)Math.Round(img.Width * (double)TargetHeight / img.Height));
            w = Math.Min(w, _options.MaxWidth);

            tensors[i] = PreprocessSingle(img, w);
            if (tensors[i].Length == 0) continue;

            widths[i] = w;
            if (w > maxWidth) maxWidth = w;
        }

        if (maxWidth == 0) return ([], widths, 0);

        // Assemble [N, 3, TargetHeight, maxWidth], fill with white (1.0f).
        var batch = new float[n * 3 * TargetHeight * maxWidth];
        Array.Fill(batch, 1.0f);

        for (int b = 0; b < n; b++)
        {
            int w = widths[b];
            if (w == 0) continue;

            var src = tensors[b];
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < TargetHeight; y++)
                {
                    int dstOff = b * 3 * TargetHeight * maxWidth
                               + c * TargetHeight * maxWidth
                               + y * maxWidth;
                    int srcOff = c * TargetHeight * w + y * w;
                    Buffer.BlockCopy(src,   srcOff * sizeof(float),
                                     batch, dstOff * sizeof(float),
                                     w * sizeof(float));
                }
            }
        }

        return (batch, widths, maxWidth);
    }

    private static unsafe float[] PreprocessSingle(SKImage image, int targetW)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(targetW, TargetHeight, SKColorType.Rgb888x, SKAlphaType.Opaque));
        if (surface is null) return [];

        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, targetW, TargetHeight));

        using var snapshot = surface.Snapshot();
        if (snapshot is null) return [];

        using var bmp = SKBitmap.FromImage(snapshot);
        if (bmp is null) return [];

        var   tensor   = new float[3 * TargetHeight * targetW];
        int   bpp      = bmp.BytesPerPixel; // 4 for Rgb888x: R G B X
        int   rowBytes = bmp.RowBytes;
        byte* ptr      = (byte*)bmp.GetPixels();

        for (int y = 0; y < TargetHeight; y++)
        {
            byte* row = ptr + (long)y * rowBytes;
            for (int x = 0; x < targetW; x++)
            {
                byte* px = row + x * bpp;
                // Normalize: (v/255 − 0.5) / 0.5
                tensor[0 * TargetHeight * targetW + y * targetW + x] = (px[0] / 255f - 0.5f) / 0.5f;
                tensor[1 * TargetHeight * targetW + y * targetW + x] = (px[1] / 255f - 0.5f) / 0.5f;
                tensor[2 * TargetHeight * targetW + y * targetW + x] = (px[2] / 255f - 0.5f) / 0.5f;
            }
        }

        return tensor;
    }

    // ── Inference ─────────────────────────────────────────────────────────────

    private IReadOnlyList<(string Text, float Score)> Infer(
        float[] batchData, int batchSize, int batchWidth, int[] widths)
    {
        var tensor = new DenseTensor<float>(
            batchData,
            [batchSize, 3, TargetHeight, batchWidth]);

        string inputName = _session.InputMetadata.Keys.First();
        var    inputs    = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

        long t0 = Stopwatch.GetTimestamp();
        using var outputs   = _session.Run(inputs);
        if (OcrProfiler.Enabled) OcrProfiler.AddInfer(Stopwatch.GetTimestamp() - t0);
        var       outTensor = outputs[0].AsTensor<float>();
        var       outData   = outTensor.ToArray();
        int       timeSteps = outTensor.Dimensions[1];
        int       numCls    = outTensor.Dimensions[2];

        var results = new (string Text, float Score)[batchSize];
        for (int b = 0; b < batchSize; b++)
        {
            if (widths[b] == 0)
            {
                results[b] = (string.Empty, 0f);
                continue;
            }
            results[b] = CtcDecode(outData, b * timeSteps * numCls, timeSteps, numCls);
        }
        return results;
    }

    // ── CTC Decode ────────────────────────────────────────────────────────────

    private (string Text, float Score) CtcDecode(
        float[] logits, int offset, int timeSteps, int numCls)
    {
        int spaceThreshold = _options.WordSpaceBlankThreshold;
        return spaceThreshold > 0
            ? CtcDecodeWithSpaceHeuristic(logits, offset, timeSteps, numCls, spaceThreshold)
            : CtcDecodeStandard(logits, offset, timeSteps, numCls);
    }

    // Standard CTC decode — use when the dict/model handles spaces natively
    // (i.e. official PP-OCRv5 with UseSpaceChar=true).
    private (string Text, float Score) CtcDecodeStandard(
        float[] logits, int offset, int timeSteps, int numCls)
    {
        var sb        = new StringBuilder(timeSteps / 2);
        double total  = 0.0;
        int    count  = 0;
        int    prev   = -1;

        for (int t = 0; t < timeSteps; t++)
        {
            int tBase  = offset + t * numCls;
            int maxIdx = ArgMax(logits, tBase, numCls);

            if (maxIdx != 0 && maxIdx != prev)
            {
                total += logits[tBase + maxIdx];
                count++;

                int ci = maxIdx - 1; // dict is 0-indexed; blank lives at output index 0
                if ((uint)ci < (uint)_chars.Count)
                    sb.Append(_chars[ci]);
            }
            prev = maxIdx;
        }

        return (sb.ToString().Trim(), count > 0 ? (float)(total / count) : 0f);
    }

    // CTC decode with blank-run space heuristic — use when the dict has no
    // space character (unofficial models, e.g. HuggingFace eslav/english).
    // A run of `threshold` or more consecutive blank timesteps is treated as
    // a word boundary and a space is inserted between the surrounding words.
    private (string Text, float Score) CtcDecodeWithSpaceHeuristic(
        float[] logits, int offset, int timeSteps, int numCls, int threshold)
    {
        var sb        = new StringBuilder(timeSteps / 2);
        double total  = 0.0;
        int    count  = 0;
        int    prev   = -1;
        int    blanks = 0; // consecutive blank timesteps since last non-blank

        for (int t = 0; t < timeSteps; t++)
        {
            int tBase  = offset + t * numCls;
            int maxIdx = ArgMax(logits, tBase, numCls);

            if (maxIdx == 0)
            {
                blanks++;
            }
            else
            {
                // Long blank run between two words → insert a space
                if (blanks >= threshold && sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                blanks = 0;

                if (maxIdx != prev)
                {
                    total += logits[tBase + maxIdx];
                    count++;

                    int ci = maxIdx - 1;
                    if ((uint)ci < (uint)_chars.Count)
                        sb.Append(_chars[ci]);
                }
            }
            prev = maxIdx;
        }

        return (sb.ToString().Trim(), count > 0 ? (float)(total / count) : 0f);
    }

    private static int ArgMax(float[] arr, int offset, int count)
    {
        int   best = 0;
        float bv   = arr[offset];
        for (int i = 1; i < count; i++)
            if (arr[offset + i] > bv) { bv = arr[offset + i]; best = i; }
        return best;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> LoadDict(string path, bool useSpaceChar)
    {
        var chars = File.ReadAllLines(path, Encoding.UTF8)
                        .Select(l => l.TrimEnd('\r'))
                        .ToList();
        // Official PP-OCRv5 models are trained with use_space_char=True.
        // Space is not stored in the dict file — PaddleOCR appends it as the
        // last output class at inference time.  We mirror that here so that
        // output index (chars.Count + 1) correctly maps to ' '.
        if (useSpaceChar && (chars.Count == 0 || chars[^1] != " "))
            chars.Add(" ");
        return chars.AsReadOnly();
    }

    private static InferenceSession CreateSession(PaddleOcrRecognizerV5Options options)
    {
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };

        // The CPU arena trades steady-state memory for speed; let the caller opt out (see options).
        so.EnableCpuMemArena = options.EnableCpuMemArena;

        if (options.UseGpu &&
            OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider"))
        {
            try
            {
                // Provider-options API (libonnxruntime_providers_shared/_cuda) instead of the legacy
                // entry point, which the Linux GPU build does not export → EntryPointNotFoundException.
                using var cudaOptions = new OrtCUDAProviderOptions();
                cudaOptions.UpdateOptions(new Dictionary<string, string>
                {
                    ["device_id"] = options.GpuId.ToString()
                });
                so.AppendExecutionProvider_CUDA(cudaOptions);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CUDA unavailable for PaddleV5, using CPU: {ex.Message}");
            }
        }

        so.AppendExecutionProvider_CPU();
        return new InferenceSession(options.ModelPath, so);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _session.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
