using DiplomaMethod.Core.Models.LayoutClassification;

namespace DiplomaMethod.Core.Services;

/// <summary>
/// Decides whether a layout detection should be kept for extraction. Applied to the classifier's
/// output before OCR so that unwanted regions are dropped cheaply, before any recognition runs.
/// A predicate (rather than a list transform) keeps callers free to filter several index-aligned
/// collections — e.g. image-space and PDF-space detections — with a single geometric decision.
/// </summary>
public interface IDetectionFilter
{
    bool Keep(DetectionResult detection);
}
