using DiplomaMethod.Core.Models;
using DiplomaMethod.Core.Services;
using SkiaSharp;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using DiplomaMethod.Core.Models.Documents;

namespace DiplomaMethod.Application.DocumentReader;

public class DocumentReader : IDocumentReader
{
    public async IAsyncEnumerable<LayoutImage> ReadDocumentAsync(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Document not found: {path}");

        var extension = Path.GetExtension(path).ToLower();

        switch (extension)
        {
            case ".pdf":
                await foreach (var image in ReadPdfAsync(path))
                    yield return image;
                break;

            case ".jpg":
            case ".jpeg":
            case ".png":
            case ".tiff":
            case ".tif":
            case ".bmp":
                await foreach (var image in ReadImageAsync(path))
                    yield return image;
                break;

            default:
                throw new NotSupportedException($"File format '{extension}' is not supported");
        }
    }

    private const double TargetPageWidthPx = 2480.0;

    private static double ComputeScale(string path)
    {
        try
        {
            using var probe = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0));
            using var page  = probe.GetPageReader(0);
            int nativeW = page.GetPageWidth();
            if (nativeW <= 0) return 300.0 / 72.0;
            double scale = TargetPageWidthPx / nativeW;
            return Math.Clamp(scale, 1.0, 300.0 / 72.0);
        }
        catch
        {
            return 150.0 / 72.0;
        }
    }

    private async IAsyncEnumerable<LayoutImage> ReadPdfAsync(string path)
    {
        // The raw PDF bytes are needed by the direct (L1) PdfPig extractor, which reads the actual
        // embedded document text — not the rendered raster. Each page gets its own seekable view over
        // the shared byte[] (no copy), so PdfDocument.Open works and per-page disposal is independent.
        var pdfBytes = await File.ReadAllBytesAsync(path);

        var scale = ComputeScale(path);
        using var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(scale));

        var pageCount = docReader.GetPageCount();

        for (int pageNum = 0; pageNum < pageCount; pageNum++)
        {
            SKImage? image = null;
            int renderedWidth = 0, renderedHeight = 0;
            try
            {
                using var pageReader = docReader.GetPageReader(pageNum);
                renderedWidth = pageReader.GetPageWidth();
                renderedHeight = pageReader.GetPageHeight();
                image = RenderPdfPageToImage(pageReader);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to render PDF page {pageNum + 1}: {ex.Message}");
            }

            if (image != null)
            {
                yield return new LayoutImage
                {
                    CachedImage = image,
                    ImageStream = new MemoryStream(pdfBytes, writable: false),
                    Page = new PageInfo
                    {
                        DocumentId = Path.GetFileNameWithoutExtension(path),
                        PageId = $"page_{pageNum + 1}",
                        PageNumber = pageNum + 1,
                        PdfPageWidthPt = renderedWidth / scale,
                        PdfPageHeightPt = renderedHeight / scale
                    }
                };
            }

            await Task.Yield();
        }
    }

    private SKImage? RenderPdfPageToImage(IPageReader page)
    {
        try
        {
            var width    = page.GetPageWidth();
            var height   = page.GetPageHeight();
            var rawBytes = page.GetImage();

            // PDFium renders with A=0 for the page background (transparent).
            // Fill with white first, then draw BGRA content on top so transparent
            // regions become white rather than black.
            using var rgbBitmap = new SKBitmap(
                new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(rgbBitmap);
            canvas.Clear(SKColors.White);

            unsafe
            {
                fixed (byte* p = rawBytes)
                {
                    using var bgraBitmap = new SKBitmap();
                    bgraBitmap.InstallPixels(
                        new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                        (IntPtr)p, width * 4);
                    canvas.DrawBitmap(bgraBitmap, 0, 0);
                }
            }

            return SKImage.FromBitmap(rgbBitmap);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to render PDF page: {ex.Message}");
            return null;
        }
    }

    private static async IAsyncEnumerable<LayoutImage> ReadImageAsync(string path)
    {
        var imageData = await File.ReadAllBytesAsync(path);

        using var codec = SKCodec.Create(new MemoryStream(imageData)) ?? throw new NotSupportedException($"Unable to decode image at '{path}': unsupported or corrupted format");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgb888x, SKAlphaType.Opaque);
        
        var bitmap = new SKBitmap(info);
        codec.GetPixels(info, bitmap.GetPixels());

        yield return new LayoutImage
        {
            CachedImage = SKImage.FromBitmap(bitmap),
            ImageStream = Stream.Null,
            Page = new PageInfo
            {
                DocumentId = Path.GetFileNameWithoutExtension(path),
                PageId = "page_1",
                PageNumber = 1
            }
        };
    }
}