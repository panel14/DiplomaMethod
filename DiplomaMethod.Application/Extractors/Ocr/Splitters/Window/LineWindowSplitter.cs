using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Window;

/// <summary>
/// Splits a text-line image into recognition-ready "windows", mirroring the training-time script
/// <c>split_lines.py</c> so inference sees the same input distribution the model was retrained on.
///
/// Steps (all at the target height, where the pixel thresholds are defined):
///   1. Resize the line to <see cref="_targetHeight"/> px (aspect-preserving).
///   2. Otsu-threshold the grayscale; mark columns that contain ink.
///   3. Group ink columns into word segments separated by gaps ≥ <see cref="_minGapPx"/> px.
///   4. Greedily pack consecutive words into windows whose width stays ≤ <see cref="_maxWidth"/> px
///      (inter-word spaces inside a window are preserved — the model must read them).
///   5. Crop each window with <see cref="_padPx"/> px horizontal padding.
///
/// A window boundary is always an inter-word gap, so the caller rejoins window texts with a space.
/// Returned images are independent copies at the target height (owned by the caller). Vertical
/// padding is NOT added here — it comes from the line splitter's top padding, matching how the
/// dataset crops were produced (LineExtractor padded before resize).
/// </summary>
public sealed class LineWindowSplitter(
    int targetHeight = 48,
    int maxWidth = 600,
    int minGapPx = 10,
    int padPx = 4,
    double minInk = 0.0) : ILineWindowSplitter
{
    private readonly int _targetHeight = Math.Max(1, targetHeight);
    private readonly int _maxWidth = Math.Max(1, maxWidth);
    private readonly int _minGapPx = Math.Max(1, minGapPx);
    private readonly int _padPx = Math.Max(0, padPx);
    private readonly double _minInk = minInk;

    public List<SKImage> Split(SKImage lineImage)
    {
        if (lineImage.Width == 0 || lineImage.Height == 0) return [];

        using SKImage line = ResizeToHeight(lineImage, _targetHeight);
        int w = line.Width;

        using var bitmap = SKBitmap.FromImage(line);
        byte[] gray = ToGrayscale(bitmap, out byte[] colMin); // flat gray + darkest pixel per column
        int thr = OtsuThreshold(gray);

        var onColumns = new bool[w];
        for (int x = 0; x < w; x++)
            onColumns[x] = colMin[x] < thr; // column has at least one ink pixel (dark < thr)

        var segments = WordSegments(onColumns, _minGapPx);
        if (segments.Count == 0)
            return [CropCopy(line, 0, w)]; // blank/solid — hand back the whole resized line

        var windows = GroupIntoWindows(segments, _maxWidth);

        var crops = new List<SKImage>(windows.Count);
        foreach (var (x0, x1) in windows)
        {
            int a = Math.Max(0, x0 - _padPx);
            int b = Math.Min(w, x1 + 1 + _padPx);
            if (b <= a) continue;

            if (_minInk > 0 && InkFraction(gray, w, thr, a, b) < _minInk)
                continue; // near-empty window (dot/dash/noise)

            crops.Add(CropCopy(line, a, b));
        }

        // Never return empty when there was ink — fall back to the whole line.
        return crops.Count > 0 ? crops : [CropCopy(line, 0, w)];
    }

    // Resizes to the target height (aspect-preserving). Returns a new SKImage owned by the caller.
    private static SKImage ResizeToHeight(SKImage src, int targetH)
    {
        int newW = Math.Max(1, (int)Math.Round(src.Width * (double)targetH / src.Height));
        using var surface = SKSurface.Create(
            new SKImageInfo(newW, targetH, SKColorType.Rgb888x, SKAlphaType.Opaque));
        surface.Canvas.Clear(SKColors.White);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell); // high-quality downscale
        surface.Canvas.DrawImage(src, new SKRect(0, 0, src.Width, src.Height),
            new SKRect(0, 0, newW, targetH), sampling);
        return surface.Snapshot();
    }

    // Copies the column range [a, b) of <paramref name="src"/> into a new independent SKImage,
    // so it stays valid after the source is disposed.
    private static SKImage CropCopy(SKImage src, int a, int b)
    {
        int w = b - a;
        int h = src.Height;
        using var surface = SKSurface.Create(
            new SKImageInfo(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(src, new SKRect(a, 0, b, h), new SKRect(0, 0, w, h));
        return surface.Snapshot();
    }

    // Flat grayscale (ITU-R 601-2 luma, matching PIL "L") + the darkest pixel per column.
    private static unsafe byte[] ToGrayscale(SKBitmap bitmap, out byte[] colMin)
    {
        int h = bitmap.Height;
        int w = bitmap.Width;
        int bpp = bitmap.BytesPerPixel;
        int rowBytes = bitmap.RowBytes;
        byte* basePtr = (byte*)bitmap.GetPixels();

        var gray = new byte[w * h];
        colMin = new byte[w];
        for (int x = 0; x < w; x++) colMin[x] = 255;

        for (int y = 0; y < h; y++)
        {
            byte* row = basePtr + (long)y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                byte* px = row + x * bpp;
                int g = (px[0] * 299 + px[1] * 587 + px[2] * 114) / 1000;
                gray[y * w + x] = (byte)g;
                if (g < colMin[x]) colMin[x] = (byte)g;
            }
        }
        return gray;
    }

    // Fraction of ink pixels (gray < thr) within the column range [a, b).
    private static double InkFraction(byte[] gray, int w, int thr, int a, int b)
    {
        int h = gray.Length / w;
        long ink = 0;
        for (int y = 0; y < h; y++)
        {
            int rowOff = y * w;
            for (int x = a; x < b; x++)
                if (gray[rowOff + x] < thr) ink++;
        }
        long area = (long)(b - a) * h;
        return area > 0 ? (double)ink / area : 0.0;
    }

    // Otsu's method over a 256-bin histogram of the grayscale values.
    private static int OtsuThreshold(byte[] gray)
    {
        var hist = new int[256];
        foreach (var v in gray) hist[v]++;

        long total = gray.Length;
        double sumTotal = 0;
        for (int i = 0; i < 256; i++) sumTotal += (double)i * hist[i];

        double sumB = 0, wB = 0, maxBetween = 0;
        int thr = 127;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            double wF = total - wB;
            if (wF == 0) break;

            sumB += (double)i * hist[i];
            double mB = sumB / wB;
            double mF = (sumTotal - sumB) / wF;
            double between = wB * wF * (mB - mF) * (mB - mF);
            if (between > maxBetween) { maxBetween = between; thr = i; }
        }
        return thr;
    }

    // Runs of "on" columns separated by gaps of >= minGap off-columns.
    private static List<(int Start, int End)> WordSegments(bool[] colOn, int minGap)
    {
        var segments = new List<(int, int)>();
        int w = colOn.Length;
        int segStart = -1, segEnd = -1, gap = 0;

        for (int x = 0; x < w; x++)
        {
            if (colOn[x])
            {
                if (segStart < 0) { segStart = x; segEnd = x; }
                else segEnd = x;
                gap = 0;
            }
            else if (segStart >= 0)
            {
                gap++;
                if (gap >= minGap) { segments.Add((segStart, segEnd)); segStart = -1; }
            }
        }
        if (segStart >= 0) segments.Add((segStart, segEnd));
        return segments;
    }

    // Greedily packs consecutive word segments into windows ≤ maxWidth (measured from the window's
    // first word start to the current word end). Inter-word gaps inside a window are kept.
    private static List<(int Start, int End)> GroupIntoWindows(List<(int Start, int End)> segs, int maxWidth)
    {
        var windows = new List<(int, int)>();
        int grpStart = segs[0].Start;
        int grpEnd = segs[0].End;

        for (int i = 1; i < segs.Count; i++)
        {
            var (s, e) = segs[i];
            if (e - grpStart + 1 <= maxWidth)
                grpEnd = e;
            else
            {
                windows.Add((grpStart, grpEnd));
                grpStart = s;
                grpEnd = e;
            }
        }
        windows.Add((grpStart, grpEnd));
        return windows;
    }
}
