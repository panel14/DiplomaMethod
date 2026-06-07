using DiplomaMethod.Core.Options;
using OpenCvSharp;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Preprosessors;

/// <summary>
/// Applies image-quality improvements to a component image before OCR.
/// All operations are performed in grayscale via OpenCV.
/// Returns null when preprocessing is disabled or produces no changes.
/// </summary>
public static class OcrPreprocessor
{
    /// <summary>
    /// Preprocesses <paramref name="source"/>: optional deskew then adaptive binarization.
    /// Returns a new <see cref="SKImage"/> owned by the caller, or null if opts.Enabled is false.
    /// </summary>
    public static SKImage? Process(SKImage source, OcrPreprocessOptions opts)
    {
        if (!opts.Enabled) return null;
        if (!opts.Deskew && !opts.AdaptiveBinarize) return null;

        using var gray = ToGrayscaleMat(source);

        Mat current  = gray;
        bool owned   = false;

        if (opts.Deskew && source.Height >= opts.MinHeightForDeskew)
        {
            double angle = EstimateSkewAngle(current, opts.MaxDeskewAngle, opts.DeskewStep);
            if (Math.Abs(angle) > opts.DeskewStep * 0.5)
            {
                var deskewed = RotateGray(current, angle);
                if (owned) current.Dispose();
                current = deskewed;
                owned   = true;
            }
        }

        if (opts.AdaptiveBinarize)
        {
            int blockSize = opts.AdaptiveBlockSize % 2 == 0
                ? opts.AdaptiveBlockSize + 1
                : opts.AdaptiveBlockSize;
            blockSize = Math.Max(3, blockSize);

            var binary = new Mat();
            Cv2.AdaptiveThreshold(
                current, binary, 255,
                AdaptiveThresholdTypes.GaussianC,
                ThresholdTypes.Binary,
                blockSize,
                opts.AdaptiveC);

            if (owned) current.Dispose();
            current = binary;
            owned   = true;
        }

        var result = MatToSkImage(current);
        if (owned) current.Dispose();
        return result;
    }

    // ── Skew correction ───────────────────────────────────────────────────────

    // Searches angles in [-maxAngle, +maxAngle] at 'step' increments.
    // Returns the angle whose horizontal projection has maximum variance
    // (text rows produce sharp peaks when the image is correctly oriented).
    private static double EstimateSkewAngle(Mat gray, float maxAngle, float step)
    {
        double bestAngle    = 0.0;
        double bestVariance = -1.0;

        for (double angle = -maxAngle; angle <= maxAngle; angle += step)
        {
            using var rotated  = RotateGray(gray, angle);
            double    variance = ComputeRowProjectionVariance(rotated);
            if (variance > bestVariance) { bestVariance = variance; bestAngle = angle; }
        }

        return bestAngle;
    }

    // Computes the variance of per-row dark-pixel counts.
    // When text is horizontal, counts spike on text rows and drop on gap rows → high variance.
    private static unsafe double ComputeRowProjectionVariance(Mat gray)
    {
        int  h    = gray.Rows;
        int  w    = gray.Cols;
        long step = (long)gray.Step();
        var  ptr  = (byte*)gray.DataPointer;

        double total = 0;
        var rowCounts = new int[h];

        for (int y = 0; y < h; y++)
        {
            byte* row  = ptr + y * step;
            int   dark = 0;
            for (int x = 0; x < w; x++)
                if (row[x] < 128) dark++;
            rowCounts[y] = dark;
            total       += dark;
        }

        double mean = total / Math.Max(1, h);
        double var  = 0;
        for (int y = 0; y < h; y++)
        {
            double d = rowCounts[y] - mean;
            var += d * d;
        }
        return var / Math.Max(1, h);
    }

    // Rotates the grayscale image by angleDeg around its centre.
    // Borders are filled with 255 (white = background).
    private static Mat RotateGray(Mat src, double angleDeg)
    {
        var center = new Point2f(src.Cols / 2f, src.Rows / 2f);
        using var M = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        var dst = new Mat();
        Cv2.WarpAffine(src, dst, M, src.Size(),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            new Scalar(255));
        return dst;
    }

    // ── Image conversion helpers ──────────────────────────────────────────────

    private static Mat ToGrayscaleMat(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        SKBitmap src       = bitmap;
        bool     converted = false;

        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            src       = bitmap.Copy(SKColorType.Bgra8888);
            converted = true;
        }

        try
        {
            using var bgraMat = Mat.FromPixelData(
                src.Height, src.Width, MatType.CV_8UC4, src.GetPixels(), src.RowBytes);
            var gray = new Mat();
            Cv2.CvtColor(bgraMat, gray, ColorConversionCodes.BGRA2GRAY);
            return gray;
        }
        finally
        {
            if (converted) src.Dispose();
        }
    }

    private static SKImage MatToSkImage(Mat mat)
    {
        Cv2.ImEncode(".png", mat, out byte[] buf);
        return SKImage.FromEncodedData(buf)!;
    }
}
