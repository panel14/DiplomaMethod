using DiplomaMethod.Core.Models.Extraction;

namespace DiplomaMethod.Core.Services;

public interface ISortingService
{
    Task<IEnumerable<TextBlock>> SortAsync(IEnumerable<TextBlock> blocks);
}
