using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using DiplomaMethod.Core.Models.Extraction;
using UglyToad.PdfPig.Content;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Base;

public sealed class TextValidator(
    IReadOnlyList<ITextStrongHeuristic> strongHeuristics,
    IReadOnlyList<ITextThresholdHeuristic> thresholdHeuristics) : ITextValidator
{
    private readonly IReadOnlyList<ITextStrongHeuristic> _strongHeuristics = strongHeuristics;
    private readonly IReadOnlyList<ITextThresholdHeuristic> _thresholdHeuristics = thresholdHeuristics;

    public bool ValidateStrongText(List<Word> words)
        => _strongHeuristics.All(h => h.IsValid(words));

    public double[] EvaluateBlock(TextBlock textBlock)
    {
        var metrics = new double[_thresholdHeuristics.Count];
        for (int i = 0; i < _thresholdHeuristics.Count; i++)
            metrics[i] = _thresholdHeuristics[i].Evaluate(textBlock);
        return metrics;
    }
}
