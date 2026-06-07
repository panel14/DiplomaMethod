using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Strong;

public sealed class WordOverlapHeuristic(double maxAllowedIoU = 0.3) : ITextStrongHeuristic
{
    private readonly double _maxAllowedIoU = maxAllowedIoU;

    public bool IsValid(List<Word> words)
    {
        for (int i = 0; i < words.Count - 1; i++)
        {
            if (ComputeIoU(words[i].BoundingBox, words[i + 1].BoundingBox) > _maxAllowedIoU)
                return false;
        }
        return true;
    }

    private static double ComputeIoU(PdfRectangle a, PdfRectangle b)
    {
        var interLeft   = Math.Max(a.Left, b.Left);
        var interBottom = Math.Max(a.Bottom, b.Bottom);
        var interRight  = Math.Min(a.Right, b.Right);
        var interTop    = Math.Min(a.Top, b.Top);

        var interArea = Math.Max(0, interRight - interLeft) *
                        Math.Max(0, interTop - interBottom);
        if (interArea == 0) return 0;

        var aArea = a.Width * a.Height;
        var bArea = b.Width * b.Height;
        return interArea / (aArea + bArea - interArea);
    }
}
