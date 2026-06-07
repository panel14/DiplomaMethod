using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;
using UglyToad.PdfPig.Content;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Strong;

public sealed class MaxCharCountHeuristic(int maxChars) : ITextStrongHeuristic
{
    private readonly int _maxChars = maxChars;

    public bool IsValid(List<Word> words)
        => words.Sum(w => w.Text.Length) <= _maxChars;
}
