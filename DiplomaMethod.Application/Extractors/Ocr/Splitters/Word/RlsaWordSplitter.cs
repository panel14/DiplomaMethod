using System.Buffers;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;

/// <summary>
/// Word-boundary detector based on the Run-Length Smoothing Algorithm (RLSA).
///
/// Algorithm:
///   1. Compute the vertical projection profile: proj[x] = dark-pixel count in column x.
///   2. Collect all ink segments and the gap widths between them.
///   3. Bimodality check: max_gap / min_gap must be ≥ 2; otherwise the distribution is
///      unimodal (all gaps similar → no detectable word boundary) and the line is returned
///      as a single unit.
///   4. K = arithmetic mean of all gap widths.
///      In normal text there are more inter-character gaps than inter-word gaps, so the
///      mean is pulled toward the smaller (character-spacing) class. This means word
///      spaces reliably land above K without a statistical estimator like Otsu.
///   5. Class-separation check: mean(gaps > K) / mean(gaps ≤ K) must be ≥ 2.0.
///      When inter-char and inter-word widths are too close (e.g. wide-tracked bold font),
///      the two resulting classes are nearly indistinguishable and splitting would produce
///      spurious cuts; this guard returns [] in that case instead of over-segmenting.
///   6. Merge phase (RLSA): merge every pair of consecutive ink segments whose gap ≤ K.
///      Remaining gaps > K are word boundaries.
///
/// Advantage over pure Otsu (AOM): the mean-based threshold is pulled toward the
/// dominant (inter-character) class, so it is naturally below the word-space range
/// even when the two distributions have a small separation ratio.
/// The explicit class-separation check prevents over-segmentation for wide-tracked
/// fonts where the ratio is close to 1.
/// </summary>
public sealed class RlsaWordSplitter : IWordSplitter
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

            int inkThreshold = Math.Max(1, height / 10);

            // ── Collect ink segments and ALL gap widths ───────────────────────
            var inkSegs   = new List<(int X0, int X1)>(16);
            var gapWidths = new List<int>(16);

            bool seenInk = false, inInk = false, inGap = false;
            int  inkX0 = -1, gapX0 = -1;

            for (int x = 0; x <= width; x++)
            {
                bool isInk = x < width && projPool[x] > inkThreshold;

                if (isInk)
                {
                    seenInk = true;
                    if (inGap) { gapWidths.Add(x - gapX0); inGap = false; }
                    if (!inInk) { inkX0 = x; inInk = true; }
                }
                else
                {
                    if (inInk)
                    {
                        inkSegs.Add((inkX0, x - 1));
                        inInk = false;
                        if (seenInk) { gapX0 = x; inGap = true; }
                    }
                    // Trailing gap is not a word boundary — intentionally not closed.
                }
            }
            if (inInk) inkSegs.Add((inkX0, width - 1));

            if (inkSegs.Count <= 1 || gapWidths.Count == 0) return [];

            // ── Bimodality check ──────────────────────────────────────────────
            int  minGap = int.MaxValue, maxGap = 0;
            long sumGap = 0;
            foreach (var g in gapWidths)
            {
                if (g < minGap) minGap = g;
                if (g > maxGap) maxGap = g;
                sumGap += g;
            }
            if (maxGap < 2 * Math.Max(1, minGap)) return []; // unimodal → no reliable split

            // ── RLSA: K = arithmetic mean of all gap widths ───────────────────
            int K = (int)Math.Round((double)sumGap / gapWidths.Count);

            // Noise guard: K must be proportionally meaningful.
            // H/12 ≈ 8% of line height; typography word spaces are ≥ 15% at any DPI.
            if (K < Math.Max(2, height / 12)) return [];

            // ── Class-separation check ────────────────────────────────────────
            // If mean(large class) / mean(small class) < 2.0, the two gap classes
            // are too close to split reliably (e.g. wide-tracked bold fonts).
            long sumSmall = 0, sumLarge = 0;
            int  cntSmall = 0, cntLarge = 0;
            foreach (var g in gapWidths)
            {
                if (g <= K) { sumSmall += g; cntSmall++; }
                else        { sumLarge += g; cntLarge++; }
            }
            if (cntSmall == 0 || cntLarge == 0) return [];

            double meanSmall = (double)sumSmall / cntSmall;
            double meanLarge = (double)sumLarge / cntLarge;
            if (meanLarge / meanSmall < 2.0) return []; // classes not well-separated

            // ── Merge phase: emit word segments ───────────────────────────────
            // Merge consecutive ink segments whose intervening gap is ≤ K.
            // Gaps > K become word boundaries.
            var result = new List<SKRectI>(4);
            int wordX0 = inkSegs[0].X0;

            for (int i = 0; i < gapWidths.Count; i++)
            {
                if (gapWidths[i] > K)
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
}
