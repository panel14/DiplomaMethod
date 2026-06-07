using System.Collections.Generic;
using DiplomaMethod.Core.Models.Extraction;

namespace DiplomaMethod.Core.Services;

public interface IMergingService
{
    Task<IEnumerable<TextBlock>> MergeAsync(IEnumerable<TextBlock> blocks);
}
