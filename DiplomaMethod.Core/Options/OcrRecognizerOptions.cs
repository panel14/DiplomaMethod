namespace DiplomaMethod.Core.Options;

public class OcrRecognizerOptions
{
    public string ModelPath { get; set; } = "./Models/paddle_ocr_rec.onnx";
    public string DictionaryPath { get; set; } = "./Models/ppocr_keys.txt";
    public int InputWidth { get; set; } = 320;
    public int InputHeight { get; set; } = 32;
    public double ConfidenceThreshold { get; set; } = 0.5;
}
