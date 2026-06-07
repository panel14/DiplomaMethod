using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DiplomaMethod.Application.Classifiers;

public class YoloLayoutClassifier(string modelPath, SessionOptions? options = null)
    : BaseOnnxClassifier(modelPath, options)
{
    private const float ConfidenceThreshold = 0.25f;
    private const float NmsIouThreshold    = 0.45f;

    public override async Task<IEnumerable<DetectionResult>> ClassifyAsync(LayoutImage image)
    {
        return await Task.Run(() =>
        {
            var (inputTensor, letterbox) = ConvertToLetterboxArray(image);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("images",
                    new DenseTensor<float>(inputTensor, [1, _options.ChannelCount,
                    _options.TargetHeight, _options.TargetWidth]))
            };

            using var outputs = _session.Run(inputs);
            var candidates = DecodeBoxes(outputs, letterbox);
            return ApplyNms(candidates);
        });
    }

    private List<DetectionResult> DecodeBoxes(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, LetterboxInfo lb)
    {
        var outputTensor = outputs.First(o => o.Name.Contains("output")).AsTensor<float>();
        int numAnchors = outputTensor.Dimensions[2];
        int numClasses = outputTensor.Dimensions[1] - 4;
        var outputArray = outputTensor.ToArray();

        List<DetectionResult> candidates = [];

        for (int i = 0; i < numAnchors; i++)
        {
            float cx = outputArray[0 * numAnchors + i];
            float cy = outputArray[1 * numAnchors + i];
            float w  = outputArray[2 * numAnchors + i];
            float h  = outputArray[3 * numAnchors + i];

            float maxConf = 0;
            int bestClass = -1;
            for (int c = 0; c < numClasses; c++)
            {
                float score = outputArray[(4 + c) * numAnchors + i];
                if (score > maxConf) { maxConf = score; bestClass = c; }
            }

            if (maxConf < ConfidenceThreshold) continue;

            float x1 = (Math.Clamp(cx - w / 2f, 0f, _options.TargetWidth)  - lb.PadX) / lb.Scale;
            float y1 = (Math.Clamp(cy - h / 2f, 0f, _options.TargetHeight) - lb.PadY) / lb.Scale;
            float x2 = (Math.Clamp(cx + w / 2f, 0f, _options.TargetWidth)  - lb.PadX) / lb.Scale;
            float y2 = (Math.Clamp(cy + h / 2f, 0f, _options.TargetHeight) - lb.PadY) / lb.Scale;

            x1 = Math.Clamp(x1, 0f, lb.OriginalWidth);
            y1 = Math.Clamp(y1, 0f, lb.OriginalHeight);
            x2 = Math.Clamp(x2, 0f, lb.OriginalWidth);
            y2 = Math.Clamp(y2, 0f, lb.OriginalHeight);

            if (x2 <= x1 || y2 <= y1) continue;

            string label = bestClass >= 0 && bestClass < ClassifierOptions.ClassLabels.Length
                ? ClassifierOptions.ClassLabels[bestClass]
                : $"Class_{bestClass}";

            candidates.Add(new DetectionResult
            {
                Label      = label,
                ClassIndex = bestClass,
                Confidence = maxConf,
                BoundingBox = new BoundingBox(x1, y1, x2 - x1, y2 - y1)
            });
        }

        return candidates;
    }

    private static List<DetectionResult> ApplyNms(List<DetectionResult> candidates)
    {
        var sorted = candidates.OrderByDescending(d => d.Confidence).ToList();
        bool[] suppressed = new bool[sorted.Count];
        var kept = new List<DetectionResult>();
        for (int i = 0; i < sorted.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(sorted[i]);
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (!suppressed[j] && IoU(sorted[i].BoundingBox, sorted[j].BoundingBox) > NmsIouThreshold)
                    suppressed[j] = true;
            }
        }
        return kept;
    }

    private static float IoU(BoundingBox a, BoundingBox b)
    {
        double ix1 = Math.Max(a.X, b.X);
        double iy1 = Math.Max(a.Y, b.Y);
        double ix2 = Math.Min(a.X + a.Width,  b.X + b.Width);
        double iy2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (ix2 <= ix1 || iy2 <= iy1) return 0f;
        double inter = (ix2 - ix1) * (iy2 - iy1);
        double union = a.Width * a.Height + b.Width * b.Height - inter;
        return union > 0 ? (float)(inter / union) : 0f;
    }
}
