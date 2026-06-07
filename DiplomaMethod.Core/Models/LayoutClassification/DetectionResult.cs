namespace DiplomaMethod.Core.Models.LayoutClassification;

public record DetectionResult
{
    public string Label { get; set; } = string.Empty;
    public int ClassIndex { get; set; } = -1;
    public double Confidence { get; set; }
    public BoundingBox BoundingBox { get; set; }
}
