using System.Buffers;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;

/// <summary>
/// Word-boundary detector based on the Adaptive Over-Split and Merge (AOM) principle.
///
/// Algorithm:
///   1. Compute the vertical projection profile: proj[x] = number of dark pixels in column x.
///   2. Identify ALL gaps between ink segments (over-split phase).
///      For each gap record its width and the maximum projection in the gap columns (depth metric).
///   3. Keep only "deep" gaps — those where every gap column is nearly empty
///      (MaxProj ≤ inkThreshold). Shallow gaps contain stroke edges of round letters
///      (о, с, е) and must not be counted as word boundaries.
///   4. Run Otsu's method on the widths of deep gaps to find the width threshold that
///      separates narrow inter-character spacing from wide inter-word spacing.
///   5. Merge (merge phase): a gap is a word boundary iff it is both DEEP and WIDE.
///      Adjacent ink segments separated by a non-boundary gap are merged into one word.
///   6. Return word bounding rectangles; return [] when no reliable boundary is found.
///
/// Advantage over pure Otsu-on-all-gaps: the 2-D analysis (width × depth) prevents
/// shallow within-letter gaps from polluting the width distribution and triggering
/// false cuts inside Cyrillic words.
/// </summary>
public sealed class AdaptiveOverSplitMergeSplitter : IWordSplitter
{
    private const int BlackPixelThreshold = 128;

    public unsafe List<SKRectI> Split(SKImage lineImage)
    {
        int width  = lineImage.Width;
        int height = lineImage.Height;
        if (width == 0 || height == 0) return [];

        var projPool = ArrayPool<int>.Shared.Rent(width);
        try
        {
            Array.Clear(projPool, 0, width);

            using var bitmap = SKBitmap.FromImage(lineImage);
            int   bpp      = bitmap.BytesPerPixel;
            int   rowBytes = bitmap.RowBytes;
            byte* basePtr  = (byte*)bitmap.GetPixels();

            for (int y = 0; y < height; y++)
            {
                byte* row = basePtr + (long)y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    byte* px = row + x * bpp;
                    if ((px[0] + px[1] + px[2]) / 3 < BlackPixelThreshold) projPool[x]++;
                }
            }

            // Ink threshold: same formula as in the line splitter — columns with
            // ≤ H/10 dark pixels are treated as non-ink (gaps).
            int inkThreshold   = Math.Max(1, height / 10);
            int depthThreshold = inkThreshold; // gap must be truly empty to qualify

            // ── Over-split phase: collect ALL gaps ─────────────────────────────
            var inkSegs = new List<(int X0, int X1)>(16);
            var gaps    = new List<(int X0, int X1, int Width, int MaxProj)>(16);

            bool seenInk      = false;
            bool inInkSegment = false;
            int  inkStart     = -1;
            bool inGap        = false;
            int  gapStart     = -1;
            int  maxInGap     = 0;

            for (int x = 0; x <= width; x++)
            {
                bool isInk = x < width && projPool[x] > inkThreshold;

                if (isInk)
                {
                    seenInk = true;
                    if (inGap)
                    {
                        gaps.Add((gapStart, x - 1, x - gapStart, maxInGap));
                        inGap     = false;
                        gapStart  = -1;
                        maxInGap  = 0;
                    }
                    if (!inInkSegment)
                    {
                        inkStart      = x;
                        inInkSegment  = true;
                    }
                }
                else
                {
                    if (inInkSegment)
                    {
                        inkSegs.Add((inkStart, x - 1));
                        inInkSegment = false;
                        if (seenInk)
                        {
                            inGap    = true;
                            gapStart = x;
                            maxInGap = 0;
                        }
                    }
                    if (inGap && x < width)
                        maxInGap = Math.Max(maxInGap, projPool[x]);
                }
            }
            if (inInkSegment) inkSegs.Add((inkStart, width - 1));
            // Trailing gap intentionally excluded — it is not a word boundary.

            if (gaps.Count == 0 || inkSegs.Count <= 1) return [];

            // ── Merge phase: classify gaps ──────────────────────────────────────

            // Width threshold derived from DEEP gaps only (MaxProj ≤ depthThreshold).
            // Shallow gaps are caused by stroke edges of round letters and must not
            // skew the distribution used to find the word-gap threshold.
            var deepWidths = new List<int>(gaps.Count);
            foreach (var (_, _, gw, mp) in gaps)
                if (mp <= depthThreshold) deepWidths.Add(gw);

            if (deepWidths.Count == 0) return [];

            int minWordGap = ComputeWordGapThreshold(deepWidths);
            if (minWordGap < 0) return [];                        // unimodal → no reliable split
            // H/12 ≈ 8% of line height. Typography word spaces are ≥ 15% at any DPI,
            // so this guards against sub-pixel noise without killing legitimate boundaries.
            if (minWordGap < Math.Max(3, height / 12)) return [];

            // Emit word segments: merge ink segments across narrow/shallow gaps.
            var result  = new List<SKRectI>(4);
            int wordX0  = inkSegs[0].X0;

            for (int i = 0; i < gaps.Count; i++)
            {
                var (_, _, gw, mp) = gaps[i];
                bool isWordBoundary = mp <= depthThreshold && gw > minWordGap;

                if (isWordBoundary)
                {
                    result.Add(new SKRectI(wordX0, 0, inkSegs[i].X1 + 1, height));
                    wordX0 = inkSegs[i + 1].X0;
                }
            }
            result.Add(new SKRectI(wordX0, 0, inkSegs[^1].X1 + 1, height));

            return result.Count > 1 ? result : [];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(projPool);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns the Otsu threshold on gap widths, or -1 if the distribution is
    // unimodal (max < 2 × min) — meaning no reliable word-boundary gap exists.
    private static int ComputeWordGapThreshold(List<int> gapWidths)
    {
        if (gapWidths.Count == 0) return -1;
        if (gapWidths.Count == 1) return gapWidths[0] - 1;

        int min = int.MaxValue, max = 0;
        foreach (var w in gapWidths)
        {
            if (w < min) min = w;
            if (w > max) max = w;
        }
        if (max < 2 * Math.Max(1, min)) return -1; // unimodal

        var arr = ArrayPool<int>.Shared.Rent(gapWidths.Count);
        try
        {
            for (int i = 0; i < gapWidths.Count; i++) arr[i] = gapWidths[i];
            return OtsuThreshold(arr, gapWidths.Count);
        }
        finally { ArrayPool<int>.Shared.Return(arr); }
    }

    private static int OtsuThreshold(int[] values, int count)
    {
        if (count == 0) return 0;
        int max = 0;
        for (int i = 0; i < count; i++) if (values[i] > max) max = values[i];
        if (max == 0) return 0;

        Span<long> hist = max <= 1024
            ? stackalloc long[max + 1]
            : new long[max + 1];
        hist.Clear();
        for (int i = 0; i < count; i++) hist[values[i]]++;

        double total = count, sumAll = 0;
        for (int v = 0; v <= max; v++) sumAll += v * hist[v];

        double sumB = 0, wB = 0, maxVar = 0;
        int best = 0;
        for (int t = 0; t <= max; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            double wF = total - wB;
            if (wF == 0) break;
            sumB += t * hist[t];
            double mB = sumB / wB;
            double mF = (sumAll - sumB) / wF;
            double v  = wB * wF * (mB - mF) * (mB - mF);
            if (v > maxVar) { maxVar = v; best = t; }
        }
        return best;
    }
}
