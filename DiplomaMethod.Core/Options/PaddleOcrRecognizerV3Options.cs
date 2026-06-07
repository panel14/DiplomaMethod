namespace DiplomaMethod.Core.Options;

public class PaddleOcrRecognizerV3Options
{
    public bool UseGpu { get; set; } = false;
    public int GpuId { get; set; } = 0;
    public int GpuInitialMemoryMb { get; set; } = 500;
}
