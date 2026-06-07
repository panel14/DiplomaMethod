namespace DiplomaMethod.Application.Extractors.Ocr.Heuristics;

/// <summary>
/// Metric: non-printable chars / total chars.
/// High value = control chars or replacement symbols dominate — likely OCR garbage.
/// </summary>
public sealed class PrintableRatioHeuristic : IStringHeuristic
{
    public double Evaluate(string text)
    {
        if (text.Length == 0) return 1.0;
        int bad = text.Count(c => char.IsControl(c) || c == '�');
        return (double)bad / text.Length;
    }
}
