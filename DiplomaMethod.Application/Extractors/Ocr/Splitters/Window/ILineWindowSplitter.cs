using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Window;

/// <summary>
/// Cuts a single text-line image into fixed-height "windows" that fit the recognition model's
/// input width, splitting only on inter-word gaps so words are never broken. Returned images are
/// already normalized to the target height and owned by the caller (must be disposed).
/// </summary>
public interface ILineWindowSplitter
{
    List<SKImage> Split(SKImage lineImage);
}
