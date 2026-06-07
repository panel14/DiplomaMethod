using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Services;

namespace DiplomaMethod.Application.Processors;

public class ParagraphMergingService : IMergingService
{
    private const int MergeThreshold = 3;
    private const string TextLabel = "Text";

    public Task<IEnumerable<TextBlock>> MergeAsync(IEnumerable<TextBlock> blocks)
    {
        var list = blocks.ToList();
        if (list.Count <= 1)
            return Task.FromResult<IEnumerable<TextBlock>>(list);

        var result = new List<TextBlock>(list.Count);
        var current = list[0];

        for (int i = 1; i < list.Count; i++)
        {
            var next = list[i];
            if (ComputeScore(current, next) >= MergeThreshold)
                current = MergeBlocks(current, next);
            else
            {
                result.Add(current);
                current = next;
            }
        }
        result.Add(current);

        return Task.FromResult<IEnumerable<TextBlock>>(result);
    }

    private static int ComputeScore(TextBlock a, TextBlock b)
    {
        if (a.Label != TextLabel || b.Label != TextLabel) return 0;

        var aLast = a.Lines.Count > 0 ? a.Lines[^1].Text.TrimEnd() : "";
        var bFirst = b.Lines.Count > 0 ? b.Lines[0].Text.TrimStart() : "";

        int score = 0;

        if (aLast.EndsWith('-'))
            score += 3;

        if (aLast.Length > 0 && !IsEndOfSentence(aLast[^1]))
            score += 1;

        if (bFirst.Length > 0 && char.IsLower(bFirst[0]))
            score += 1;

        double gap = b.Box.Y - (a.Box.Y + a.Box.Height);
        double avgLineHeight = GetAvgLineHeight(a, b);
        if (gap >= 0 && avgLineHeight > 0 && gap < avgLineHeight)
            score += 1;

        return score;
    }

    private static TextBlock MergeBlocks(TextBlock a, TextBlock b)
    {
        var aLines = a.Lines.ToList();
        var bLines = b.Lines.ToList();

        // Join hyphenated word across blocks
        if (aLines.Count > 0 && bLines.Count > 0 && aLines[^1].Text.TrimEnd().EndsWith('-'))
        {
            var trimmed = aLines[^1].Text.TrimEnd();
            aLines[^1] = aLines[^1] with { Text = trimmed[..^1] + bLines[0].Text.TrimStart() };
            bLines.RemoveAt(0);
        }

        double minX = Math.Min(a.Box.X, b.Box.X);
        double minY = Math.Min(a.Box.Y, b.Box.Y);
        double maxX = Math.Max(a.Box.X + a.Box.Width, b.Box.X + b.Box.Width);
        double maxY = Math.Max(a.Box.Y + a.Box.Height, b.Box.Y + b.Box.Height);

        return new TextBlock
        {
            Lines = [.. aLines, .. bLines],
            Box = new BoundingBox(minX, minY, maxX - minX, maxY - minY),
            Label = a.Label,
            Confidence = (a.Confidence + b.Confidence) / 2.0
        };
    }

    private static bool IsEndOfSentence(char c) => c is '.' or '!' or '?' or ':' or ';';

    private static double GetAvgLineHeight(TextBlock a, TextBlock b) =>
        a.Lines.Concat(b.Lines)
            .Where(l => l.Box.Height > 0)
            .Select(l => l.Box.Height)
            .DefaultIfEmpty(0)
            .Average();
}
