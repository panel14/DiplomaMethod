using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Line
{
    public interface ILineSplitter
    {
        List<SKRectI> Split(SKImage blockImage);
    }
}
