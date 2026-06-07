using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Line;

/// <summary>
/// Text-line detector based on Linear Segment Linking (LSL).
///
/// Algorithm:
///   1. Scan each row and collect horizontal "dark runs" (contiguous dark-pixel sequences).
///   2. Link runs in adjacent rows that overlap horizontally using Union-Find.
///      Each connected component is one topological "ink island" (typically a letter or cluster).
///   3. Merge components whose Y-intervals overlap — they belong to the same text line.
///   4. Return each merged Y-interval as a full-width horizontal strip (same format as HPP).
///
/// Advantages over horizontal projection profiling:
///   - Letter height is preserved exactly (no threshold-induced trimming of ascenders/descenders).
///   - Tolerant of minor document skew (~10–15°) because runs link per-row, not globally.
/// </summary>
public sealed class LinearSegmentLinkingSplitter(int topPaddingPx = 0) : ILineSplitter
{
    private const int BlackPixelThreshold = 128;
    private const int MinComponentHeightPx = 3;
    private const int MinComponentWidthPx  = 2;

    // Extra rows added above each detected line so capitals/ascenders are not clipped (clamped to
    // the image top). Mirrors the LineExtractor dataset tool so inference matches how training crops
    // were produced. 0 = tight strips (legacy behaviour).
    private readonly int _topPaddingPx = Math.Max(0, topPaddingPx);

    public unsafe List<SKRectI> Split(SKImage blockImage)
    {
        int H = blockImage.Height;
        int W = blockImage.Width;
        if (H == 0 || W == 0) return [];

        using var bitmap = SKBitmap.FromImage(blockImage);
        int   bpp      = bitmap.BytesPerPixel;
        int   rowBytes = bitmap.RowBytes;
        byte* basePtr  = (byte*)bitmap.GetPixels();

        // ── Step 1: collect horizontal runs per row ───────────────────────────
        // Each run: (x0 inclusive, x1 inclusive, global sequential id)
        var rowRuns = new List<(int X0, int X1, int Id)>[H];
        for (int y = 0; y < H; y++) rowRuns[y] = [];

        int totalRuns = 0;
        for (int y = 0; y < H; y++)
        {
            byte* row    = basePtr + (long)y * rowBytes;
            bool  inRun  = false;
            int   runX0  = 0;
            for (int x = 0; x < W; x++)
            {
                byte* px   = row + x * bpp;
                bool  dark = (px[0] + px[1] + px[2]) / 3 < BlackPixelThreshold;
                if (dark && !inRun)       { runX0 = x; inRun = true; }
                else if (!dark && inRun)  { rowRuns[y].Add((runX0, x - 1, totalRuns++)); inRun = false; }
            }
            if (inRun) rowRuns[y].Add((runX0, W - 1, totalRuns++));
        }

        if (totalRuns == 0) return [];

        // ── Step 2: Union-Find (with path compression) ────────────────────────
        var parent = new int[totalRuns];
        for (int i = 0; i < totalRuns; i++) parent[i] = i;

        // ── Step 3: link runs in adjacent rows that overlap horizontally ──────
        // Both row lists are sorted by X0 (naturally, since we scan left→right).
        // Use a two-pointer sweep: advance the "previous" pointer as the "current"
        // run's X0 grows — O(runs_per_row) per row pair.
        for (int y = 1; y < H; y++)
        {
            var prev = rowRuns[y - 1];
            var curr = rowRuns[y];
            if (prev.Count == 0 || curr.Count == 0) continue;

            int p = 0;
            for (int c = 0; c < curr.Count; c++)
            {
                var (cx0, cx1, cid) = curr[c];
                // Skip prev runs that end before current run starts
                while (p < prev.Count && prev[p].X1 < cx0) p++;
                // Link all prev runs whose X0 is still within current run
                for (int pp = p; pp < prev.Count && prev[pp].X0 <= cx1; pp++)
                    Union(parent, cid, prev[pp].Id);
            }
        }

        // ── Step 4: compute tight bounding boxes per component root ───────────
        var boxes = new Dictionary<int, (int Y0, int Y1, int X0, int X1)>(totalRuns / 4 + 1);
        for (int y = 0; y < H; y++)
        {
            foreach (var (x0, x1, id) in rowRuns[y])
            {
                int root = Find(parent, id);
                if (boxes.TryGetValue(root, out var b))
                    boxes[root] = (Math.Min(b.Y0, y), Math.Max(b.Y1, y),
                                   Math.Min(b.X0, x0), Math.Max(b.X1, x1));
                else
                    boxes[root] = (y, y, x0, x1);
            }
        }

        // ── Step 5: filter noise components ───────────────────────────────────
        var components = new List<(int Y0, int Y1)>(boxes.Count);
        foreach (var (y0, y1, x0, x1) in boxes.Values)
        {
            if (y1 - y0 + 1 >= MinComponentHeightPx && x1 - x0 + 1 >= MinComponentWidthPx)
                components.Add((y0, y1));
        }

        if (components.Count == 0) return [];

        // ── Step 6: merge components with overlapping Y-intervals ─────────────
        // Components from the same text line share overlapping Y ranges.
        // Sort by Y0 and sweep: merge when the next component starts within
        // the current group's Y range.
        components.Sort((a, b) => a.Y0.CompareTo(b.Y0));

        var result = new List<SKRectI>(8);
        var (gy0, gy1) = components[0];

        for (int i = 1; i < components.Count; i++)
        {
            var (cy0, cy1) = components[i];
            if (cy0 <= gy1) // Y-intervals overlap → same text line
            {
                gy1 = Math.Max(gy1, cy1);
            }
            else
            {
                result.Add(MakeStrip(gy0, gy1, W));
                (gy0, gy1) = (cy0, cy1);
            }
        }
        result.Add(MakeStrip(gy0, gy1, W));

        return result;
    }

    // Builds a full-width strip for a line's [y0, y1] Y-interval, grown upward by the configured
    // top padding (clamped to the image top) to preserve capitals/ascenders.
    private SKRectI MakeStrip(int y0, int y1, int width)
        => new(0, Math.Max(0, y0 - _topPaddingPx), width, y1 + 1);

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b) parent[a] = b;
    }
}
