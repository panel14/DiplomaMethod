namespace DiplomaMethod.Application.Extractors.Ocr.Heuristics;

/// <summary>Metric: uppercase chars / total chars. High value = abnormally many capitals.</summary>
public sealed class UpperSymbolsStringHeuristic : IStringHeuristic
{
    public double Evaluate(string text)
    {
        if (text.Length == 0) return 1.0;
        int upperCount = text.Count(char.IsUpper);
        return (double)upperCount / text.Length;
    }
}
