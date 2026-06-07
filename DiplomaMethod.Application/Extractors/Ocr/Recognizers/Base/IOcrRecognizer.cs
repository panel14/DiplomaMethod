using SkiaSharp;

namespace DiplomaMethod.Application.Extractors.Ocr.Recognizer.Base
{
    public interface IOcrRecognizer
    {
        Task<string> RecognizeLineAsync(SKImage lineImage);
    }
}
