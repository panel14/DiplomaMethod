using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Services;

namespace DiplomaMethod.Application.Processors;

public class ParagraphMergingService : IMergingService
{
    private const string TextLabel = "Text";

    // Geometric gate for a merge: the two blocks must share a column (horizontal overlap) and be
    // vertically adjacent. Continuation hyphens/case only confirm a merge that geometry already allows.
    private const double MinHorizontalOverlap = 0.5;   // fraction of the narrower block's width
    private const double MaxVerticalGapFactor = 1.5;   // gap below this × avg line height = adjacent
    private const double MaxVerticalOverlapFactor = 1.0; // reject blocks overlapping more than this × line height

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
            if (ShouldMerge(current, next))
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

    private static bool ShouldMerge(TextBlock a, TextBlock b)
    {
        if (a.Label != TextLabel || b.Label != TextLabel) return false;
        if (a.Lines.Count == 0 || b.Lines.Count == 0) return false;

        // ── Geometric gate (necessary) ──────────────────────────────────────────
        // Same column: blocks must overlap horizontally. Two-column layouts have ~0 overlap, so a
        // left-column block can never merge with a right-column one regardless of text cues.
        if (HorizontalOverlapFraction(a.Box, b.Box) < MinHorizontalOverlap) return false;

        double avgLineHeight = GetAvgLineHeight(a, b);
        if (avgLineHeight <= 0) return false;

        // Vertically adjacent: a small gap (continuation) or slight overlap, but not a large jump
        // and not a heavy overlap (which signals duplicate/nested boxes, not a continuation).
        double gap = b.Box.Y - (a.Box.Y + a.Box.Height);
        if (gap > avgLineHeight * MaxVerticalGapFactor) return false;
        if (gap < -avgLineHeight * MaxVerticalOverlapFactor) return false;

        // ── Textual continuation cue (at least one required) ────────────────────
        var aLast  = a.Lines[^1].Text.TrimEnd();
        var bFirst = b.Lines[0].Text.TrimStart();

        bool hyphen        = aLast.EndsWith('-');
        bool noSentenceEnd = aLast.Length > 0 && !IsEndOfSentence(aLast[^1]);
        bool lowerStart    = bFirst.Length > 0 && char.IsLower(bFirst[0]);

        return hyphen || (noSentenceEnd && lowerStart);
    }

    // Fraction of the narrower block's width that overlaps the other horizontally (0..1).
    private static double HorizontalOverlapFraction(BoundingBox a, BoundingBox b)
    {
        double inter = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        if (inter <= 0) return 0;
        double minW = Math.Min(a.Width, b.Width);
        return minW > 0 ? inter / minW : 0;
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
