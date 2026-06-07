using DiplomaMethod.Core.Models.Extraction;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;

public interface ITextThresholdHeuristic
{
    /// <summary>Returns a raw metric in [0..1]. Higher means more suspicious / lower quality.</summary>
    double Evaluate(TextBlock block);
}
