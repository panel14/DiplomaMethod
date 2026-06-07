using DiplomaMethod.Core.Models.LayoutClassification;

namespace DiplomaMethod.Core.Models.Extraction
{
    public record TextLine
    {
        public required string Text { get; init; }
        public required BoundingBox Box { get; init; }
    }
}
