namespace DiplomaMethod.Core.Utils
{
    public static class MathUtils
    {
        public static int[] GetPeaks(ReadOnlySpan<double> lineBorders)
        {
            if (lineBorders == null || lineBorders.Length < 3)
            {
                return [];
            }

            // Using an array pool to sort the span
            var sortedPool = System.Buffers.ArrayPool<double>.Shared.Rent(lineBorders.Length);
            var sortedSpan = sortedPool.AsSpan(0, lineBorders.Length);
            lineBorders.CopyTo(sortedSpan);
            sortedSpan.Sort();

            double q1 = sortedSpan[(int)(sortedSpan.Length * 0.25)];
            double q3 = sortedSpan[(int)(sortedSpan.Length * 0.75)];
            double iqr = q3 - q1;

            double threshold = Math.Max(iqr * 1.5, 5.0);
            double lowerBound = q1 - threshold;
            double upperBound = q3 + threshold;

            var peaks = new List<int>();
            for (int i = 0; i < lineBorders.Length; i++)
            {
                if (lineBorders[i] < lowerBound || lineBorders[i] > upperBound)
                {
                    peaks.Add(i);
                }
            }

            System.Buffers.ArrayPool<double>.Shared.Return(sortedPool);
            return [.. peaks];
        }
    }
}
