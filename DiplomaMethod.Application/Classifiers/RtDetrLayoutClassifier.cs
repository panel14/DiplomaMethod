using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace DiplomaMethod.Application.Classifiers;

public class RtDetrLayoutClassifier(string modelPath, SessionOptions? options = null) : BaseOnnxClassifier(modelPath, options)
{
    private const float ConfidenceThreshold = 0.5f;

    private List<DetectionResult> GenerateResults(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var labels = outputs.First(o => o.Name == "labels").AsEnumerable<long>();
        var scores = outputs.First(o => o.Name == "scores").AsEnumerable<float>();
        var boxes = outputs.First(o => o.Name == "boxes").AsEnumerable<float>();

        List<DetectionResult> results = [];

        int detectionCount = labels.Count();
        for (int i = 0; i < detectionCount; i++)
        {
            float confidence = scores.ElementAt(i);

            if (confidence < ConfidenceThreshold)
                continue;

            long classIndex = labels.ElementAt(i);
            string label = classIndex >= 0 && classIndex < ClassifierOptions.ClassLabels.Length
                ? ClassifierOptions.ClassLabels[classIndex]
                : "Unknown";

            int boxStartIdx = i * 4;
            float x1 = boxes.ElementAt(boxStartIdx);
            float y1 = boxes.ElementAt(boxStartIdx + 1);
            float x2 = boxes.ElementAt(boxStartIdx + 2);
            float y2 = boxes.ElementAt(boxStartIdx + 3);

            float pixelX = x1 * _options.TargetWidth;
            float pixelY = y1 * _options.TargetHeight;
            float pixelWidth = (x2 - x1) * _options.TargetWidth;
            float pixelHeight = (y2 - y1) * _options.TargetHeight;

            var boundingBox = new BoundingBox(pixelX, pixelY, pixelWidth, pixelHeight);

            results.Add(new DetectionResult
            {
                Label = label,
                Confidence = confidence,
                BoundingBox = boundingBox,
            });
        }

        return results;
    }

    public override async Task<IEnumerable<DetectionResult>> ClassifyAsync(LayoutImage image)
    {
        return await Task.Run(() =>
        {
            float[] inputTensor = ConvertToArray(image);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("images",
                    new DenseTensor<float>(inputTensor, [1, _options.ChannelCount, _options.TargetHeight, _options.TargetWidth]))
            };

            using var outputs = _session.Run(inputs);
            return GenerateResults(outputs);
        });
    }
}
