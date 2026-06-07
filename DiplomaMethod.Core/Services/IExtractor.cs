using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;

namespace DiplomaMethod.Core.Services;

public interface IExtractor
{
    Task<IReadOnlyList<TextBlock>> ReadAsync(LayoutImage image, IReadOnlyList<DetectionResult> detection);
}
