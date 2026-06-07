using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using DiplomaMethod.Core.Models.Extraction;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Threshold;

/// <summary>
/// Metric: lines / chars. High value means lines are abnormally short (suspicious segmentation).
/// </summary>
public sealed class LineCountHeuristic : ITextThresholdHeuristic
{
    public double Evaluate(TextBlock block)
    {
        int charCount = block.Lines.Sum(l => l.Text.Length);
        if (charCount == 0) return 1.0;

        return Math.Min(1.0, (double)block.Lines.Count / charCount);
    }
}
