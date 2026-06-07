namespace DiplomaMethod.Core.Options.TrOcr
{
    public class TrOcrImageOptions
    {
        public float[] Mean { get; set; } = [0.5f, 0.5f, 0.5f];
        public float[] Std { get; set; } = [0.5f, 0.5f, 0.5f];
        public int TargetWidth { get; set; } = 384;
        public int TargetHeight { get; set; } = 384;
    }
}
