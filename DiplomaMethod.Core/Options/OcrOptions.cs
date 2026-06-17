namespace DiplomaMethod.Core.Options;

public class OcrOptions
{
    public float MinPaddleScore         { get; set; } = 0.87f;
    public WordSegmentationMode WordSegmentation { get; set; } = WordSegmentationMode.None;
    public OcrPreprocessOptions Preprocess { get; set; } = new();

    // When enabled, each non-atomic detection box is sub-clustered into components via Docstrum
    // before line splitting. Disabled by default: the layout detection box is treated as a single
    // component, so a block stays whole instead of being broken into 1–2 word fragments.
    public bool UseDocstrumClustering { get; set; } = false;

    // Max images per Paddle inference call. Larger batches run faster but the ONNX CPU arena sizes
    // itself to the batch's peak activation memory and holds it — so this is the main memory/speed
    // knob. 32 keeps most of the batching speed-up at a fraction of the peak memory of 128.
    public int MaxInferenceBatch { get; set; } = 32;
}
