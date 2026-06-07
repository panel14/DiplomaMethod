namespace DiplomaMethod.Core.Options;

public class TesseractOptions
{
    public string TessDataPath { get; set; } = "tessdata";
    public string Language { get; set; } = "rus+eng";
    // Tesseract PageSegMode: 7 = SingleLine (line crops), 6 = SingleBlock (paragraph crops)
    public int PageSegMode { get; set; } = 7;
    // Upscale line images shorter than this height — Tesseract accuracy degrades below ~30px
    public int UpscaleMinHeight { get; set; } = 32;
}
