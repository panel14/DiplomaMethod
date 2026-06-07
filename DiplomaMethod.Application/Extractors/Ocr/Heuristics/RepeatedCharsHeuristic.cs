namespace DiplomaMethod.Application.Extractors.Ocr.Heuristics;

/// <summary>
/// Metric: chars inside runs of ≥ minRunLength identical consecutive chars / total chars.
/// High value = model is repeating itself — typical OCR hallucination.
/// </summary>
public sealed class RepeatedCharsHeuristic(int minRunLength = 3) : IStringHeuristic
{
    private readonly int _minRunLength = minRunLength;

    public double Evaluate(string text)
    {
        if (text.Length == 0) return 1.0;

        int suspicious = 0;
        int i = 0;
        while (i < text.Length)
        {
            int run = 1;
            while (i + run < text.Length && text[i + run] == text[i])
                run++;
            if (run >= _minRunLength)
                suspicious += run;
            i += run;
        }

        return Math.Min(1.0, (double)suspicious / text.Length);
    }
}
