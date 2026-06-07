using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Services;

namespace DiplomaMethod.Application.Filters;

/// <summary>
/// Drops detections whose bounding box is much taller than it is wide
/// (<c>height / width >= maxHeightToWidthRatio</c>).
///
/// Such tall, narrow regions are almost always vertical / rotated marginalia — journal references,
/// arXiv ids or copyright notices printed sideways along a page edge — not body text. Rotating them
/// back is impractical (the rotation direction is ambiguous and genuinely vertical labels would turn
/// to noise), and their content is peripheral to the document body, so the safest action for a
/// knowledge base is to discard them. Body text, titles and section headers are wide, so they are
/// unaffected; the check is purely geometric and runs before OCR, saving recognition work.
///
/// A ratio of 0 or less disables the filter (everything is kept).
/// </summary>
public sealed class AspectRatioDetectionFilter(double maxHeightToWidthRatio = 3.0) : IDetectionFilter
{
    private readonly double _maxRatio = maxHeightToWidthRatio;

    public bool Keep(DetectionResult detection)
    {
        if (_maxRatio <= 0) return true;

        double w = detection.BoundingBox.Width;
        double h = detection.BoundingBox.Height;
        if (w <= 0) return true; // degenerate box — can't judge, leave it for downstream handling

        return h / w < _maxRatio;
    }
}
