using System.Buffers;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Utils;

namespace DiplomaMethod.Application.Extractors.Analyzers;

public static class TextBlockAnalyzer
{
    public static List<TextBlock> SplitByLineIndent(TextBlock block, string label)
    {
        if (block.Lines.Count == 0) return [];

        var result = new List<TextBlock>();
        var lines = block.Lines.ToList();

        var leftBordersPool = ArrayPool<double>.Shared.Rent(lines.Count);
        var rightBordersPool = ArrayPool<double>.Shared.Rent(lines.Count);
        var lineGapsPool = ArrayPool<double>.Shared.Rent(lines.Count);

        try
        {
            var leftBorders = leftBordersPool.AsSpan(0, lines.Count);
            var rightBorders = rightBordersPool.AsSpan(0, lines.Count);
            var lineGaps = lineGapsPool.AsSpan(0, lines.Count);

            lineGaps[0] = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                leftBorders[i] = l.Box.X;
                rightBorders[i] = l.Box.X + l.Box.Width;

                if (i > 0)
                    lineGaps[i] = Math.Abs(lines[i - 1].Box.Y - l.Box.Y);
            }

            var indentPeaks = MathUtils.GetPeaks(leftBorders);
            var trailingPeaks = MathUtils.GetPeaks(rightBorders);
            var gapPeaks = MathUtils.GetPeaks(lineGaps);

            int currentStart = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                bool isIndent = indentPeaks.Contains(i);
                bool prevIsTrailing = i > 0 && trailingPeaks.Contains(i - 1);
                bool isGap = gapPeaks.Contains(i);

                if (i > currentStart && (isGap || (isIndent && prevIsTrailing)))
                {
                    result.Add(BuildBlock(lines, currentStart, i, label));
                    currentStart = i;
                }
            }

            if (currentStart < lines.Count)
                result.Add(BuildBlock(lines, currentStart, lines.Count, label));

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(leftBordersPool);
            ArrayPool<double>.Shared.Return(rightBordersPool);
            ArrayPool<double>.Shared.Return(lineGapsPool);
        }
    }

    private static TextBlock BuildBlock(List<TextLine> lines, int start, int end, string label)
    {
        var blockLines = lines.GetRange(start, end - start);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var l in blockLines)
        {
            if (l.Box.X < minX) minX = l.Box.X;
            if (l.Box.Y < minY) minY = l.Box.Y;
            if (l.Box.X + l.Box.Width > maxX) maxX = l.Box.X + l.Box.Width;
            if (l.Box.Y + l.Box.Height > maxY) maxY = l.Box.Y + l.Box.Height;
        }

        return new TextBlock
        {
            Lines = blockLines,
            Label = label,
            Box = new BoundingBox(minX, minY, maxX - minX, maxY - minY)
        };
    }
}
