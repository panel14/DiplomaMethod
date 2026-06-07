using DiplomaMethod.Core.Models.Extraction;
using UglyToad.PdfPig.Content;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;

public interface ITextValidator
{
    /// <summary>Runs all strong heuristics. Returns false if any fails (hard gate).</summary>
    bool ValidateStrongText(List<Word> words);

    /// <summary>
    /// Runs all threshold heuristics and returns their raw metrics.
    /// Index i corresponds to the i-th heuristic passed at construction.
    /// </summary>
    double[] EvaluateBlock(TextBlock textBlock);
}
