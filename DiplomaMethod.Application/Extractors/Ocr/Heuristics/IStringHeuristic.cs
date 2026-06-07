namespace DiplomaMethod.Application.Extractors.Ocr.Heuristics;

public interface IStringHeuristic
{
    /// <summary>Returns a raw metric in [0..1]. Higher means more suspicious / lower quality.</summary>
    double Evaluate(string text);
}
