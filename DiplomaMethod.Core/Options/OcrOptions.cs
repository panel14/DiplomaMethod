namespace DiplomaMethod.Core.Options;

public class OcrOptions
{
    public float MinPaddleScore         { get; set; } = 0.87f;
    public WordSegmentationMode WordSegmentation { get; set; } = WordSegmentationMode.None;
    public OcrPreprocessOptions Preprocess { get; set; } = new();
}
