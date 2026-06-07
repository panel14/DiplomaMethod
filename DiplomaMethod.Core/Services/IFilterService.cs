using System.Collections.Generic;
using DiplomaMethod.Core.Models.Extraction;

namespace DiplomaMethod.Core.Services;

public interface IFilterService
{
    Task<IEnumerable<TextBlock>> FilterAsync(IEnumerable<TextBlock> blocks);
}
