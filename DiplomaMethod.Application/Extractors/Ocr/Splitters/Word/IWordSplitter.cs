using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Splitters.Word
{
    public interface IWordSplitter
    {
        List<SKRectI> Split(SKImage lineImage);
    }
}
