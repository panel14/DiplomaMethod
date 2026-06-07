using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using DiplomaMethod.Core.Models.Extraction;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Threshold;

/// <summary>
/// Metric: uppercase chars / total chars. High value means abnormally many capitals (garbled text).
/// </summary>
public sealed class UpperSymbolsThreshold : ITextThresholdHeuristic
{
    public double Evaluate(TextBlock block)
    {
        var text = block.Accumulate();
        if (text.Length == 0) return 1.0;

        int upperCount = text.Count(char.IsUpper);
        return (double)upperCount / text.Length;
    }
}
