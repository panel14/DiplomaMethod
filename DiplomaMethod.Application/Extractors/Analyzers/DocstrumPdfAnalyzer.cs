using UglyToad.PdfPig.Content;

namespace DiplomaMethod.Application.Extractors.Analyzers
{
    public static class DocstrumPdfAnalyzer
    {
        private const int K = 5;
        private const double HorizontalMultiplier = 2.5; 
        private const double VerticalMultiplier = 1.6;

        public static List<List<Word>> ClusterWords(IReadOnlyList<Word> words)
        {
            if (words == null || words.Count == 0) return [];
            if (words.Count == 1) return [[words[0]]];

            var neighbors = FindKNearestNeighborsFast(words, K);

            int maxDistances = words.Count * K;
            var horizPool = System.Buffers.ArrayPool<double>.Shared.Rent(maxDistances);
            var vertPool = System.Buffers.ArrayPool<double>.Shared.Rent(maxDistances);

            try
            {
                int horizCount = 0;
                int vertCount = 0;

                for (int i = 0; i < words.Count; i++)
                {
                    var wordNeighbors = neighbors[i];
                    for (int j = 0; j < wordNeighbors.Length; j++)
                    {
                        int n = wordNeighbors[j];
                        if (n < 0) continue;

                        double dist = GetDistance(words[i], words[n]);
                        double angle = GetAngle(words[i], words[n]);

                        if (IsHorizontal(angle))
                            horizPool[horizCount++] = dist;
                        else if (IsVertical(angle))
                            vertPool[vertCount++] = dist;
                    }
                }

                double peakCharacterSpacing = GetMedianFast(horizPool.AsSpan(0, horizCount)); 
                double peakLineSpacing = GetMedianFast(vertPool.AsSpan(0, vertCount));

                if (peakCharacterSpacing <= 0 || peakLineSpacing <= 0)
                {
                    double sumHeight = 0;
                    for (int i = 0; i < words.Count; i++)
                    {
                        sumHeight += words[i].BoundingBox.Height;
                    }
                    double avgHeight = sumHeight / words.Count;

                    if (peakCharacterSpacing <= 0) peakCharacterSpacing = avgHeight * 0.2;
                    if (peakLineSpacing <= 0) peakLineSpacing = avgHeight;
                }

                double horizontalThreshold = peakCharacterSpacing * HorizontalMultiplier;
                double verticalThreshold = peakLineSpacing * VerticalMultiplier;

                var uf = new UnionFind(words.Count);

                for (int i = 0; i < words.Count; i++)
                {
                    var wordNeighbors = neighbors[i];
                    for (int j = 0; j < wordNeighbors.Length; j++)
                    {
                        int n = wordNeighbors[j];
                        if (n < 0) continue;

                        double dist = GetDistance(words[i], words[n]);
                        double angle = GetAngle(words[i], words[n]);

                        bool connect = false;
                        if (IsHorizontal(angle) && dist <= horizontalThreshold)
                        {
                            connect = true;
                        }
                        else if (IsVertical(angle) && dist <= verticalThreshold)
                        {
                            if (HasHorizontalOverlap(words[i], words[n]))
                            {
                                connect = true;
                            }
                        }

                        if (connect)
                        {
                            uf.Union(i, n);
                        }
                    }
                }

                var clusters = uf.GetClusters();
                var result = new List<List<Word>>();

                foreach (var cluster in clusters.Values)
                {
                    var clusterList = new List<Word>(cluster.Count);
                    for (int i = 0; i < cluster.Count; i++)
                    {
                        clusterList.Add(words[cluster[i]]);
                    }
                    result.Add(clusterList);
                }

                return result;
            }
            finally
            {
                System.Buffers.ArrayPool<double>.Shared.Return(horizPool);
                System.Buffers.ArrayPool<double>.Shared.Return(vertPool);
            }
        }

        #region Математика и Геометрия

        private static int[][] FindKNearestNeighborsFast(IReadOnlyList<Word> words, int k)
        {
            var neighbors = new int[words.Count][];

            Span<double> topDistances = stackalloc double[k];

            for (int i = 0; i < words.Count; i++)
            {
                neighbors[i] = new int[k];
                for (int m = 0; m < k; m++) neighbors[i][m] = -1;

                topDistances.Fill(double.MaxValue);

                for (int j = 0; j < words.Count; j++)
                {
                    if (i == j) continue;

                    double d = GetDistance(words[i], words[j]);

                    if (d < topDistances[k - 1])
                    {
                        int insertPos = k - 1;
                        while (insertPos > 0 && d < topDistances[insertPos - 1])
                        {
                            insertPos--;
                        }

                        for (int m = k - 1; m > insertPos; m--)
                        {
                            topDistances[m] = topDistances[m - 1];
                            neighbors[i][m] = neighbors[i][m - 1];
                        }

                        topDistances[insertPos] = d;
                        neighbors[i][insertPos] = j;
                    }
                }
            }
            return neighbors;
        }

        private static double GetDistance(Word w1, Word w2)
        {
            var b1 = w1.BoundingBox;
            var b2 = w2.BoundingBox;

            double dx = Math.Max(0, Math.Max(b1.Left - b2.Right, b2.Left - b1.Right));
            double dy = Math.Max(0, Math.Max(b1.Bottom - b2.Top, b2.Bottom - b1.Top));

            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double GetAngle(Word w1, Word w2)
        {
            var c1x = w1.BoundingBox.Left + w1.BoundingBox.Width / 2;
            var c1y = w1.BoundingBox.Bottom + w1.BoundingBox.Height / 2;

            var c2x = w2.BoundingBox.Left + w2.BoundingBox.Width / 2;
            var c2y = w2.BoundingBox.Bottom + w2.BoundingBox.Height / 2;

            double dx = c2x - c1x;
            double dy = c2y - c1y;
            return Math.Atan2(dy, dx) * 180 / Math.PI;
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

        private static bool HasHorizontalOverlap(Word w1, Word w2)
        {
            var b1 = w1.BoundingBox;
            var b2 = w2.BoundingBox;
            return Math.Max(b1.Left, b2.Left) <= Math.Min(b1.Right, b2.Right);
        }

        private static double GetMedianFast(Span<double> values)
        {
            if (values.Length == 0) return 0;
            values.Sort();
            int mid = values.Length / 2;
            return values.Length % 2 != 0 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
        }
        #endregion

        #region Структура данных Disjoint-Set (Union-Find)
        private class UnionFind(int size)
        {
            private readonly int[] _parent = Enumerable.Range(0, size).ToArray();

            public int Find(int i)
            {
                if (_parent[i] == i) return i;
                return _parent[i] = Find(_parent[i]);
            }

            public void Union(int i, int j)
            {
                int rootI = Find(i);
                int rootJ = Find(j);
                if (rootI != rootJ) _parent[rootI] = rootJ;
            }

            public Dictionary<int, List<int>> GetClusters()
            {
                var clusters = new Dictionary<int, List<int>>();
                for (int i = 0; i < _parent.Length; i++)
                {
                    int root = Find(i);
                    if (!clusters.ContainsKey(root)) clusters[root] = new List<int>();
                    clusters[root].Add(i);
                }
                return clusters;
            }
        }
        #endregion
    }
}