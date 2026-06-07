using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Postprocessors;

/// <summary>
/// Removes spurious line strips returned by LSL. The line detector occasionally emits a thin,
/// near-empty strip in the gap between two real lines (a stray mark, descender fragment or scan
/// artifact that survives the noise filter but doesn't overlap any line in Y). Such strips are
/// far shorter than genuine text lines.
///
/// Strategy: take the <b>median</b> line height (robust to these outliers — the mean would be
/// dragged down by the very strips we want to drop), then discard any strip whose height is below
/// <c>median * keepRatio</c>. Filtering is skipped when there are too few lines for the median to
/// be meaningful, and never returns an empty result.
/// </summary>
public sealed class MedianHeightLineFilter(double keepRatio = 0.5, int minLinesForFiltering = 3)
    : ILinePostprocessor
{
    private readonly double _keepRatio = keepRatio;
    private readonly int _minLinesForFiltering = Math.Max(1, minLinesForFiltering);

    public List<SKRectI> Process(IReadOnlyList<SKRectI> lines)
    {
        if (lines.Count < _minLinesForFiltering)
            return [.. lines];

        double minHeight = Median(lines) * _keepRatio;

        var result = new List<SKRectI>(lines.Count);
        foreach (var r in lines)
            if (r.Height >= minHeight)
                result.Add(r);

        // Guard against a degenerate threshold wiping everything out.
        return result.Count > 0 ? result : [.. lines];
    }

    private static double Median(IReadOnlyList<SKRectI> lines)
    {
        var heights = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++) heights[i] = lines[i].Height;
        Array.Sort(heights);

        int mid = heights.Length / 2;
        return heights.Length % 2 == 1
            ? heights[mid]
            : (heights[mid - 1] + heights[mid]) / 2.0;
    }
}
