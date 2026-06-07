using DiplomaMethod.Core.Models.LayoutClassification;
using OpenCvSharp;
using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Analyzers;

public static class DocstrumImageAnalyzer
{
    private const int K = 10;
    private const double MaxAspectRatio = 30.0;
    private const double MaxGapDensity  = 0.05;

    public static List<BoundingBox> ClusterComponents(SKImage blockImage)
    {
        if (blockImage.Width == 0 || blockImage.Height == 0) return [];

        using var gray   = ToGrayscaleMat(blockImage);
        using var binary = Binarize(gray);

        int maxComponentArea = Math.Min(50_000, blockImage.Width * blockImage.Height / 2);
        var components = FindAdaptiveComponents(binary, maxComponentArea);
        if (components.Count == 0) return [];

        int minGapWidth = Math.Max(8, blockImage.Width / 50);
        var columnGaps  = FindColumnGaps(binary, blockImage.Width, minGapWidth);

        return components.Count switch
        {
            1 => components,
            _ => ApplyDocstrum(components, columnGaps)
        };
    }

    private static Mat ToGrayscaleMat(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        SKBitmap src = bitmap;
        bool converted = false;

        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            src = bitmap.Copy(SKColorType.Bgra8888);
            converted = true;
        }

        try
        {
            using var bgraMat = Mat.FromPixelData(src.Height, src.Width, MatType.CV_8UC4, src.GetPixels(), src.RowBytes);
            var gray = new Mat();
            Cv2.CvtColor(bgraMat, gray, ColorConversionCodes.BGRA2GRAY);
            return gray;
        }
        finally
        {
            if (converted) src.Dispose();
        }
    }

    private static Mat Binarize(Mat gray)
    {
        var binary = new Mat();
        var mean   = Cv2.Mean(gray).Val0;
        var threshType = mean >= 128
            ? ThresholdTypes.BinaryInv | ThresholdTypes.Otsu
            : ThresholdTypes.Binary    | ThresholdTypes.Otsu;
        Cv2.Threshold(gray, binary, 0, 255, threshType);
        return binary;
    }

    private static List<BoundingBox> FindAdaptiveComponents(Mat binary, int maxArea)
    {
        using var labels    = new Mat();
        using var stats     = new Mat();
        using var centroids = new Mat();

        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids);
        if (n <= 1) return [];

        var raw = new List<(BoundingBox Box, int Area)>(n);
        for (int i = 1; i < n; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area > maxArea) continue;

            int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
            int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
            int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);

            if (Math.Max(w, h) / (double)Math.Max(1, Math.Min(w, h)) > MaxAspectRatio) continue;

            raw.Add((new BoundingBox(x, y, w, h), area));
        }

        if (raw.Count == 0) return [];

        int minArea = ComputeAdaptiveMinArea(raw);
        var result  = new List<BoundingBox>(raw.Count);
        foreach (var (box, area) in raw)
            if (area >= minArea) result.Add(box);

        return result;
    }

    private static int ComputeAdaptiveMinArea(List<(BoundingBox Box, int Area)> components)
    {
        var heights = components.Select(c => (double)c.Box.Height).OrderBy(h => h).ToArray();
        int q1 = heights.Length / 4;
        int q3 = heights.Length * 3 / 4;

        double iqMean = 0;
        for (int i = q1; i < q3; i++) iqMean += heights[i];
        iqMean /= Math.Max(1, q3 - q1);

        double minEdge = iqMean * 0.25;
        return Math.Max(4, (int)(minEdge * minEdge));
    }


    private static List<BoundingBox> ApplyDocstrum(
        List<BoundingBox> components,
        List<(int Left, int Right)> columnGaps)
    {
        var neighbors = FindKNearestNeighbors(components, K);
        int capacity  = components.Count * K;

        var horizPool = System.Buffers.ArrayPool<double>.Shared.Rent(capacity);
        var vertPool  = System.Buffers.ArrayPool<double>.Shared.Rent(capacity);

        try
        {
            int horizCount = 0, vertCount = 0;

            for (int i = 0; i < components.Count; i++)
            {
                foreach (int nb in neighbors[i])
                {
                    if (nb < 0) continue;
                    double dist  = GetDistance(components[i], components[nb]);
                    double angle = GetAngle   (components[i], components[nb]);
                    if      (IsHorizontal(angle)) horizPool[horizCount++] = dist;
                    else if (IsVertical  (angle)) vertPool [vertCount++]  = dist;
                }
            }

            var horizSpan = horizPool.AsSpan(0, horizCount);
            var vertSpan  = vertPool .AsSpan(0, vertCount);
            horizSpan.Sort();
            vertSpan .Sort();

            double horizThreshold = ComputePercentileThreshold(horizSpan, 0.90);
            double vertThreshold  = ComputeJenksBreak(vertSpan);

            if (horizThreshold <= 0 || vertThreshold <= 0)
            {
                double sumH = 0;
                foreach (var c in components) sumH += c.Height;
                double avgH = sumH / components.Count;
                if (horizThreshold <= 0) horizThreshold = avgH * 1.5;
                if (vertThreshold  <= 0) vertThreshold  = avgH * 2.0;
            }

            var uf = new UnionFind(components.Count);

            for (int i = 0; i < components.Count; i++)
            {
                foreach (int nb in neighbors[i])
                {
                    if (nb < 0) continue;
                    double dist  = GetDistance(components[i], components[nb]);
                    double angle = GetAngle   (components[i], components[nb]);

                    if (IsHorizontal(angle) && dist <= horizThreshold
                        && !CrossesAnyGap(components[i], components[nb], columnGaps))
                        uf.Union(i, nb);
                    else if (IsVertical(angle) && dist <= vertThreshold
                        && HasHorizontalOverlap(components[i], components[nb]))
                        uf.Union(i, nb);
                }
            }

            var clusters = uf.GetClusters();
            var result   = new List<BoundingBox>(clusters.Count);

            foreach (var cluster in clusters.Values)
            {
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (int idx in cluster)
                {
                    var b = components[idx];
                    if (b.X < minX) minX = b.X;
                    if (b.Y < minY) minY = b.Y;
                    double rx = b.X + b.Width, ry = b.Y + b.Height;
                    if (rx > maxX) maxX = rx;
                    if (ry > maxY) maxY = ry;
                }

                result.Add(new BoundingBox(minX, minY, maxX - minX, maxY - minY));
            }

            return result;
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(horizPool);
            System.Buffers.ArrayPool<double>.Shared.Return(vertPool);
        }
    }

    private static double ComputePercentileThreshold(Span<double> sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int idx = Math.Min(sorted.Length - 1, (int)(sorted.Length * p));
        return sorted[idx];
    }

    private static double ComputeJenksBreak(Span<double> sorted)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length < 10) return sorted[^1] * 1.2;

        double totalSum = 0, totalSumSq = 0;
        foreach (var v in sorted) { totalSum += v; totalSumSq += v * v; }

        double bestWss  = double.MaxValue;
        int    bestSplit = -1;
        double runSum = 0, runSumSq = 0;

        int minClass = Math.Max(2, sorted.Length / 10);

        for (int i = 1; i < sorted.Length; i++)
        {
            runSum   += sorted[i - 1];
            runSumSq += sorted[i - 1] * sorted[i - 1];

            if (i < minClass || i > sorted.Length - minClass) continue;

            int    n1  = i,              n2  = sorted.Length - i;
            double rs  = totalSum   - runSum;
            double rss = totalSumSq - runSumSq;
            double wss = runSumSq - runSum * runSum / n1
                       + rss     - rs     * rs     / n2;

            if (wss < bestWss) { bestWss = wss; bestSplit = i; }
        }

        if (bestSplit > 0 && bestSplit < sorted.Length
            && sorted[bestSplit] >= Math.Max(1.0, sorted[bestSplit - 1]) * 2.0)
            return (sorted[bestSplit - 1] + sorted[bestSplit]) / 2.0;

        return ComputePercentileThreshold(sorted, 0.90);
    }

    private static List<(int Left, int Right)> FindColumnGaps(
        Mat binary, int imageWidth, int minGapWidth)
    {
        int width  = binary.Width;
        int height = binary.Height;
        if (width == 0 || height == 0) return [];

        int densityThreshold = (int)(height * MaxGapDensity);
        int marginX          = imageWidth / 10;

        var profile = new int[width];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (binary.At<byte>(y, x) > 0) profile[x]++;

        var gaps     = new List<(int Left, int Right)>();
        int gapStart = -1;

        for (int x = 0; x < width; x++)
        {
            if (profile[x] <= densityThreshold)
            {
                if (gapStart < 0) gapStart = x;
            }
            else
            {
                if (gapStart >= 0 && x - gapStart >= minGapWidth)
                    gaps.Add((gapStart, x - 1));
                gapStart = -1;
            }
        }
        if (gapStart >= 0 && width - gapStart >= minGapWidth)
            gaps.Add((gapStart, width - 1));

        return [.. gaps.Where(g => g.Left > marginX && g.Right < imageWidth - marginX)];
    }

    private static bool CrossesAnyGap(
        BoundingBox b1, BoundingBox b2,
        List<(int Left, int Right)> gaps)
    {
        if (gaps.Count == 0) return false;
        double cx1   = b1.X + b1.Width  / 2.0;
        double cx2   = b2.X + b2.Width  / 2.0;
        double minCx = Math.Min(cx1, cx2);
        double maxCx = Math.Max(cx1, cx2);
        foreach (var (gl, gr) in gaps)
        {
            double centre = (gl + gr) / 2.0;
            if (centre > minCx && centre < maxCx) return true;
        }
        return false;
    }

    private static int[][] FindKNearestNeighbors(List<BoundingBox> components, int k)
    {
        int n          = components.Count;
        var neighbors  = new int[n][];
        Span<double> topDist = stackalloc double[k];

        for (int i = 0; i < n; i++)
        {
            neighbors[i] = new int[k];
            for (int m = 0; m < k; m++) neighbors[i][m] = -1;
            topDist.Fill(double.MaxValue);

            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                double d = GetDistance(components[i], components[j]);

                if (d < topDist[k - 1])
                {
                    int pos = k - 1;
                    while (pos > 0 && d < topDist[pos - 1]) pos--;
                    for (int m = k - 1; m > pos; m--)
                    {
                        topDist[m]      = topDist[m - 1];
                        neighbors[i][m] = neighbors[i][m - 1];
                    }
                    topDist[pos]      = d;
                    neighbors[i][pos] = j;
                }
            }
        }

        return neighbors;
    }

    private static double GetDistance(BoundingBox b1, BoundingBox b2)
    {
        double dx = Math.Max(0, Math.Max(b1.X - (b2.X + b2.Width),  b2.X - (b1.X + b1.Width)));
        double dy = Math.Max(0, Math.Max(b1.Y - (b2.Y + b2.Height), b2.Y - (b1.Y + b1.Height)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double GetAngle(BoundingBox b1, BoundingBox b2)
    {
        double dx = b2.X + b2.Width  / 2.0 - (b1.X + b1.Width  / 2.0);
        double dy = b2.Y + b2.Height / 2.0 - (b1.Y + b1.Height / 2.0);
        return Math.Atan2(dy, dx) * 180.0 / Math.PI;
    }

    private static bool IsHorizontal(double angle)
    {
        double a = Math.Abs(angle);
        return a <= 40 || a >= 140;
    }

    private static bool IsVertical(double angle)
    {
        double a = Math.Abs(angle);
        return a > 40 && a < 140;
    }

    private static bool HasHorizontalOverlap(BoundingBox b1, BoundingBox b2)
        => Math.Max(b1.X, b2.X) <= Math.Min(b1.X + b1.Width, b2.X + b2.Width);

    private sealed class UnionFind
    {
        private readonly int[] _parent;

        public UnionFind(int size)
        {
            _parent = new int[size];
            for (int i = 0; i < size; i++) _parent[i] = i;
        }

        public int Find(int i)
        {
            if (_parent[i] == i) return i;
            return _parent[i] = Find(_parent[i]);
        }

        public void Union(int i, int j)
        {
            int ri = Find(i), rj = Find(j);
            if (ri != rj) _parent[ri] = rj;
        }

        public Dictionary<int, List<int>> GetClusters()
        {
            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < _parent.Length; i++)
            {
                int root = Find(i);
                if (!clusters.TryGetValue(root, out var list))
                    clusters[root] = list = [];
                list.Add(i);
            }
            return clusters;
        }
    }
}
