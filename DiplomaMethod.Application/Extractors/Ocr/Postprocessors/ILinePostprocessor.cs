using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Postprocessors;

/// <summary>
/// Post-processes the line regions produced by an <c>ILineSplitter</c> before they are
/// cropped and recognized — e.g. removing spurious strips that don't correspond to real text lines.
/// </summary>
public interface ILinePostprocessor
{
    List<SKRectI> Process(IReadOnlyList<SKRectI> lines);
}
