using SkiaSharp;

namespace DiplomaMethod.Core.Models.Documents;

public class LayoutImage : IDisposable
{
    public Stream ImageStream { get; set; } = Stream.Null;
    public PageInfo Page { get; set; } = new();
    public SKImage? CachedImage { get; set; }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        ImageStream?.Dispose();
        CachedImage?.Dispose();
    }
}
