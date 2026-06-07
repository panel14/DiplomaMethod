using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using UglyToad.PdfPig.Content;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Strong;

public sealed class ControlCharHeuristic(double threshold) : ITextStrongHeuristic
{
    private readonly double _threshold = threshold;

    public bool IsValid(List<Word> words)
    {
        var letters = words.SelectMany(w => w.Letters).ToList();
        if (letters.Count == 0) return false;

        int controlCount = letters.Count(l => char.IsControl(l.Value[0]));
        return (double)controlCount / letters.Count <= _threshold;
    }
}
