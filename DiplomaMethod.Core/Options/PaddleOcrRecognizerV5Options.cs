namespace DiplomaMethod.Core.Options;

public class PaddleOcrRecognizerV5Options
{
    /// <summary>Path to rec.onnx (e.g. cyrillic/rec.onnx or english/rec.onnx).</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>Path to dict.txt — one UTF-8 character per line, no blank token.</summary>
    public string DictPath { get; set; } = string.Empty;

    /// <summary>
    /// Set to true for official PP-OCRv5 models converted from PaddlePaddle.
    /// PaddleOCR trains with use_space_char=True: the space character is NOT
    /// stored in dict.txt but is appended as the last output class at inference time.
    /// When true, LoadDict appends ' ' so the C# decoder maps index N+1 → space.
    /// </summary>
    public bool UseSpaceChar { get; set; } = false;

    public bool UseGpu { get; set; } = true;
    public int  GpuId  { get; set; } = 0;

    /// <summary>
    /// ONNX Runtime CPU memory arena. When true (default) ORT keeps a reusable memory pool that
    /// grows to the peak batch's activation size and is not released until the session is disposed —
    /// fastest, but the plateau memory stays high. Set to false to let ORT free intermediate buffers
    /// between inference calls: markedly lower steady-state memory at a small per-call overhead.
    /// </summary>
    public bool EnableCpuMemArena { get; set; } = true;

    /// <summary>
    /// Maximum width (px) an image is scaled to before inference.
    /// Acts as a safety cap for unusually wide line crops.
    /// </summary>
    public int MaxWidth { get; set; } = 1280;

    /// <summary>
    /// Number of consecutive CTC blank timesteps that are treated as a word
    /// boundary and cause a space to be inserted into the output.
    /// <para>
    /// Use this only with models whose dictionary does NOT contain a space
    /// character (e.g. the unofficial HuggingFace eslav/english models).
    /// For official PP-OCRv5 models converted with use_space_char=True, set
    /// this to 0 (disabled) and set <see cref="UseSpaceChar"/> = true instead.
    /// </para>
    /// 0 = disabled (default). Recommended value when enabled: 4–6.
    /// </summary>
    public int WordSpaceBlankThreshold { get; set; } = 0;
}
