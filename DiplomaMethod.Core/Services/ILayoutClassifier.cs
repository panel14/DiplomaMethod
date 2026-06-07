using System.Collections.Generic;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;

namespace DiplomaMethod.Core.Services;

public interface ILayoutClassifier
{
    Task<IEnumerable<DetectionResult>> ClassifyAsync(LayoutImage image);
}
