using UglyToad.PdfPig.Content;

namespace DiplomaMethod.Application.Extractors.Direct.Heuristics.Base.Interfaces;

public interface ITextStrongHeuristic
{
    bool IsValid(List<Word> words);
}
