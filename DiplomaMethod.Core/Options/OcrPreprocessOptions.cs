namespace DiplomaMethod.Core.Options;

public class OcrPreprocessOptions
{
    // Master switch — preprocessing is OFF by default
    public bool Enabled { get; set; } = false;

    // Skew correction via horizontal projection-profile search
    public bool  Deskew             { get; set; } = true;
    public int   MinHeightForDeskew { get; set; } = 60;   // skip components shorter than this
    public float MaxDeskewAngle     { get; set; } = 5.0f; // search range ± degrees
    public float DeskewStep         { get; set; } = 0.5f; // angular resolution in degrees

    // Adaptive Gaussian binarization (text → BLACK=0, background → WHITE=255)
    public bool   AdaptiveBinarize  { get; set; } = true;
    public int    AdaptiveBlockSize { get; set; } = 31;   // must be odd, ≥ 3; local window in px
    public double AdaptiveC         { get; set; } = 10.0; // subtracted from local Gaussian mean

    // Upscale individual line images before sending to OCR engines
    public int LineUpscaleMinHeight    { get; set; } = 32; // skip lines already taller than this
    public int LineUpscaleTargetHeight { get; set; } = 64; // target height after scaling
}
