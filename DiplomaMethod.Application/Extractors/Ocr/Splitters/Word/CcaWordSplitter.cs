using OpenCvSharp;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;

/// <summary>
/// Word-boundary detector based on Connected Component Analysis (CCA).
///
/// Algorithm:
///   1. Binarize the line image via Otsu's global threshold (ink = 255).
///   2. Find all connected components (8-connectivity).
///   3. Discard noise: components with height &lt; H/6 or area &lt; 4 px².
///   4. Sort component bounding intervals (left, right) by left edge.
///   5. Merge components separated by ≤ 2 px (diacritics, digitisation noise).
///   6. Collect gap widths between consecutive merged clusters.
///   7. Bimodality check: max_gap / min_gap must be ≥ 2.
///   8. K = Otsu threshold on the gap-width distribution.
///      Otsu finds the optimal split between the inter-character class
///      (many narrow gaps) and the inter-word class (few wide gaps), placing
///      the threshold between the two peaks rather than at their mean.
///      A slightly-wider intra-word gap (e.g. before a narrow terminal "и")
///      stays below K because Otsu is pulled toward the class boundary, not
///      the arithmetic mean.
///   9. Minimum-size guard: K must be ≥ H/12 to avoid sub-pixel noise splits.
///  10. Class-presence check: gaps must exist on both sides of K.
///  11. Merge clusters across gaps ≤ K; remaining gaps &gt; K are word boundaries.
///
/// Why CCA beats projection methods for Cyrillic text:
///   Thin vertical strokes within letters (diagonal of "и", serifs of serif fonts,
///   crossbar of "т") appear as low-projection columns in 1-D profiles and are
///   incorrectly classified as inter-letter gaps.  In CCA those strokes are part
///   of the connected letter blob and never appear in the gap list at all.
///
/// Why Otsu beats arithmetic mean (K) for the gap threshold:
///   Arithmetic mean is pulled toward the dominant (inter-character) class and
///   can fall below slightly-wider intra-word gaps (e.g. the gap before a narrow
///   terminal "и" in "территории").  Otsu places the threshold between the two
///   gap classes, so those intra-word outliers remain in the small-gap class.
/// </summary>
public sealed class CcaWordSplitter : IWordSplitter
{
    private const int MergeGapPx = 2; // merge components separated by ≤ this many pixels

    public List<SKRectI> Split(SKImage lineImage)
    {
        int width  = lineImage.Width;
        int height = lineImage.Height;
        if (width == 0 || height == 0) return [];

        // ── 1. Convert to binary Mat (ink = 255, background = 0) ─────────────
        using var gray   = ToGrayscaleMat(lineImage);
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        // ── 2. Connected components ───────────────────────────────────────────
        using var labelsMat    = new Mat();
        using var statsMat     = new Mat();
        using var centroidsMat = new Mat();
        int numLabels = Cv2.ConnectedComponentsWithStats(
            binary, labelsMat, statsMat, centroidsMat,
            PixelConnectivity.Connectivity8, MatType.CV_32S);

        if (numLabels <= 1) return []; // only background

        // ── 3. Collect (left, right) for each non-background component ────────
        int minHeight = Math.Max(2, height / 6);
        var spans = new List<(int Left, int Right)>(numLabels - 1);

        for (int i = 1; i < numLabels; i++) // label 0 = background
        {
            int cx = statsMat.At<int>(i, 0); // CC_STAT_LEFT
            int cw = statsMat.At<int>(i, 2); // CC_STAT_WIDTH
            int ch = statsMat.At<int>(i, 3); // CC_STAT_HEIGHT
            int ca = statsMat.At<int>(i, 4); // CC_STAT_AREA

            if (ch < minHeight || ca < 4 || cw < 1) continue;
            spans.Add((cx, cx + cw - 1));
        }

        if (spans.Count == 0) return [];
        spans.Sort(static (a, b) => a.Left.CompareTo(b.Left));

        // ── 4. Merge near-touching spans (diacritics / digitisation noise) ────
        var clusters = new List<(int Left, int Right)>(spans.Count);
        var (cL, cR) = spans[0];
        for (int i = 1; i < spans.Count; i++)
        {
            var (sL, sR) = spans[i];
            if (sL <= cR + MergeGapPx)
                cR = Math.Max(cR, sR);
            else
            {
                clusters.Add((cL, cR));
                (cL, cR) = (sL, sR);
            }
        }
        clusters.Add((cL, cR));

        if (clusters.Count <= 1) return [];

        // ── 5. Gap widths between consecutive clusters ────────────────────────
        var gapWidths = new List<int>(clusters.Count - 1);
        for (int i = 1; i < clusters.Count; i++)
        {
            int gap = clusters[i].Left - clusters[i - 1].Right - 1;
            if (gap > 0) gapWidths.Add(gap);
        }

        if (gapWidths.Count == 0) return [];

        // ── 6. Bimodality check ───────────────────────────────────────────────
        int minGap = int.MaxValue, maxGap = 0;
        foreach (var g in gapWidths)
        {
            if (g < minGap) minGap = g;
            if (g > maxGap) maxGap = g;
        }
        if (maxGap < 2 * Math.Max(1, minGap)) return [];

        // ── 7. K = Otsu threshold on the gap-width distribution ───────────────
        // Otsu places the split between the two gap classes (inter-char vs
        // inter-word) at the point that maximises between-class variance, rather
        // than at the arithmetic mean.  This correctly keeps slightly-wider
        // intra-word gaps (e.g. before a narrow terminal "и") in the small class.
        int K = OtsuThreshold(gapWidths);

        // Guard: threshold must be proportionally meaningful relative to line height.
        // H/12 ≈ 8% of height; word spaces in any font are ≥ 15%.
        if (K < Math.Max(2, height / 12)) return [];

        // ── 8. Class-presence check ───────────────────────────────────────────
        bool hasSmall = false, hasLarge = false;
        foreach (var g in gapWidths)
        {
            if (g <= K) hasSmall = true;
            else        hasLarge = true;
            if (hasSmall && hasLarge) break;
        }
        if (!hasSmall || !hasLarge) return [];

        // ── 9. Emit word rectangles ───────────────────────────────────────────
        var result   = new List<SKRectI>(4);
        int wordLeft = clusters[0].Left;

        for (int i = 1; i < clusters.Count; i++)
        {
            int gap = clusters[i].Left - clusters[i - 1].Right - 1;
            if (gap <= 0) continue;

            if (gap > K)
            {
                result.Add(new SKRectI(wordLeft, 0, clusters[i - 1].Right + 1, height));
                wordLeft = clusters[i].Left;
            }
        }
        result.Add(new SKRectI(wordLeft, 0, clusters[^1].Right + 1, height));

        return result.Count > 1 ? result : [];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Otsu's method on a list of non-negative integers.
    // Returns the threshold that maximises between-class variance.
    // Returns 0 for an empty or constant list.
    private static int OtsuThreshold(List<int> values)
    {
        if (values.Count == 0) return 0;
        int max = 0;
        foreach (var v in values) if (v > max) max = v;
        if (max == 0) return 0;

        Span<long> hist = max <= 1024
            ? stackalloc long[max + 1]
            : new long[max + 1];
        hist.Clear();
        foreach (var v in values) hist[v]++;

        double total  = values.Count;
        double sumAll = 0;
        for (int v = 0; v <= max; v++) sumAll += v * hist[v];

        double sumB = 0, wB = 0, maxVar = 0;
        int    best = 0;
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

    private static unsafe Mat ToGrayscaleMat(SKImage image)
    {
        using var bmp = SKBitmap.FromImage(image);
        int w   = bmp.Width;
        int h   = bmp.Height;
        int bpp = bmp.BytesPerPixel;
        int row = bmp.RowBytes;

        var mat = new Mat(h, w, MatType.CV_8UC1);
        byte* src     = (byte*)bmp.GetPixels();
        byte* dst     = (byte*)mat.DataPointer;
        int   dstStep = (int)mat.Step();

        for (int y = 0; y < h; y++)
        {
            byte* srcRow = src + (long)y * row;
            byte* dstRow = dst + (long)y * dstStep;
            for (int x = 0; x < w; x++)
            {
                byte* px = srcRow + x * bpp;
                dstRow[x] = (byte)((px[0] + px[1] + px[2]) / 3);
            }
        }
        return mat;
    }
}
