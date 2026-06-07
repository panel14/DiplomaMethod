namespace DiplomaMethod.Core.Options;

public class FlorenceOcrOptions
{
    // Directory where Florence-2 ONNX models are stored (or will be downloaded to).
    // Models total ~880 MB: VisionEncoder, Encoder, DecoderMerged, EmbedTokens.
    public string ModelPath { get; set; } = "./models/florence2";
    // Download models automatically on first use if they are missing.
    public bool AutoDownload { get; set; } = true;
}
