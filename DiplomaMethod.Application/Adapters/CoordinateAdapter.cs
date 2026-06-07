using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;

namespace DiplomaMethod.Application.Adapters;

public sealed class CoordinateAdapter(int yoloWidth, int yoloHeight, int imageWidth, int imageHeight)
{
    private readonly double _sx = (double)imageWidth / yoloWidth; // YOLO → image X scale
    private readonly double _sy = (double)imageHeight / yoloHeight; // YOLO → image Y scale

    public int ImageWidth { get; } = imageWidth;
    public int ImageHeight { get; } = imageHeight;

    public static CoordinateAdapter ForImage(LayoutImage image, ClassifierOptions options)
    {
        int w = image.CachedImage?.Width ?? options.TargetWidth;
        int h = image.CachedImage?.Height ?? options.TargetHeight;
        return new CoordinateAdapter(options.TargetWidth, options.TargetHeight, w, h);
    }

    public static CoordinateAdapter ForImagePixels(LayoutImage image, ClassifierOptions options)
    {
        int w = image.CachedImage?.Width ?? options.TargetWidth;
        int h = image.CachedImage?.Height ?? options.TargetHeight;
        return new CoordinateAdapter(w, h, w, h);
    }

    public BoundingBox ToImagePixels(BoundingBox yoloBbox)
        => new(_sx * yoloBbox.X, _sy * yoloBbox.Y, _sx * yoloBbox.Width, _sy * yoloBbox.Height);

    public IReadOnlyList<DetectionResult> MapToImageSpace(IReadOnlyList<DetectionResult> detections)
    {
        if (_sx == 1.0 && _sy == 1.0) return detections;

        var result = new DetectionResult[detections.Count];
        for (int i = 0; i < detections.Count; i++)
            result[i] = detections[i] with { BoundingBox = ToImagePixels(detections[i].BoundingBox) };
        return result;
    }

    public IReadOnlyList<DetectionResult> MapToPdfSpace(
        IReadOnlyList<DetectionResult> detections, double pdfWidthPt, double pdfHeightPt)
    {
        var result = new DetectionResult[detections.Count];
        for (int i = 0; i < detections.Count; i++)
        {
            var imgBox = ToImagePixels(detections[i].BoundingBox);
            result[i] = detections[i] with { BoundingBox = ToPdfPoints(imgBox, pdfWidthPt, pdfHeightPt) };
        }
        return result;
    }

    private BoundingBox ToPdfPoints(BoundingBox imgBox, double pdfWidthPt, double pdfHeightPt)
    {
        double sx = pdfWidthPt / ImageWidth;
        double sy = pdfHeightPt / ImageHeight;

        double x = imgBox.X * sx;
        double w = imgBox.Width * sx;
        double h = imgBox.Height * sy;
        double y = pdfHeightPt - (imgBox.Y + imgBox.Height) * sy;

        return new BoundingBox(x, y, w, h);
    }
}
