using DiplomaMethod.Core.Models.Documents;

namespace DiplomaMethod.Core.Services;

public interface IDocumentReader
{
    IAsyncEnumerable<LayoutImage> ReadDocumentAsync(string path);
}
