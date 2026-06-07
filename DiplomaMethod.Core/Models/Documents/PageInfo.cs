namespace DiplomaMethod.Core.Models.Documents;

public class PageInfo
{
    public string DocumentId { get; set; } = string.Empty;
    public string PageId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    // PDF page dimensions in points (0 for non-PDF sources)
    public double PdfPageWidthPt { get; set; }
    public double PdfPageHeightPt { get; set; }
}
