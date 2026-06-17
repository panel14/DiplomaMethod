using System.Threading.Tasks.Dataflow;
using DiplomaMethod.Application.Adapters;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Models.LayoutClassification;
using DiplomaMethod.Core.Options;
using DiplomaMethod.Core.Services;

namespace DiplomaMethod.Application.Pipeline;

public class TextExtractionPipeline(
    IDocumentReader documentReader,
    ILayoutClassifier layoutClassifier,
    IExtractor manualExtractor,
    IExtractor ocrExtractor,
    IExtractor? florenceExtractor,
    IExtractor tableExtractor,
    ISortingService sortingService,
    IMergingService mergingService,
    ClassifierOptions classifierOptions,
    IDetectionFilter? detectionFilter = null,
    Action<string>? logger = null)
{
    private readonly IDocumentReader _documentReader = documentReader;
    private readonly ILayoutClassifier _layoutClassifier = layoutClassifier;
    private readonly IExtractor _manualExtractor = manualExtractor;
    private readonly IExtractor _ocrExtractor = ocrExtractor;
    private readonly IExtractor? _florenceExtractor = florenceExtractor;
    private readonly IExtractor _tableExtractor = tableExtractor;
    private readonly ISortingService _sortingService = sortingService;
    private readonly IMergingService _mergingService = mergingService;
    private readonly ClassifierOptions _classifierOptions = classifierOptions;
    private readonly IDetectionFilter? _detectionFilter = detectionFilter;
    private readonly Action<string> _log = logger ?? (_ => { });

    private const double ValidBlockConfidence = 0.75;
    private const string TableLabel = "Table";

    public Task<List<TextBlock>> ProcessDocumentAsync(LayoutImage image)
        => RunPipelineAsync(FeedSingle(image));

    public Task<List<TextBlock>> ProcessDocumentFromFileAsync(string filePath)
        => RunPipelineAsync(_documentReader.ReadDocumentAsync(filePath));

    private async Task<List<TextBlock>> RunPipelineAsync(IAsyncEnumerable<LayoutImage> pages)
    {
        var results = new List<TextBlock>();
        var resultsLock = new object();

        var blockOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            BoundedCapacity = 16
        };

        var classifyOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            BoundedCapacity = 16
        };

        var classifyBlock    = new TransformBlock<LayoutImage, PipelineData>(ClassifyLayoutAsync, classifyOptions);
        var tableBlock       = new TransformBlock<PipelineData, PipelineData>(ExtractTablesAsync, blockOptions);
        var manualBlock      = new TransformBlock<PipelineData, PipelineData>(ExtractManualAsync, blockOptions);
        var ocrBlock         = new TransformBlock<PipelineData, PipelineData>(ExtractOcrAsync, blockOptions);
        var florenceBlock    = new TransformBlock<PipelineData, PipelineData>(ExtractFlorenceAsync, blockOptions);
        var postProcessBlock = new ActionBlock<PipelineData>(
            data => PostProcessAsync(data, results, resultsLock), blockOptions);

        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };
        classifyBlock.LinkTo(tableBlock, linkOptions);
        tableBlock.LinkTo(manualBlock, linkOptions);
        manualBlock.LinkTo(ocrBlock, linkOptions);
        ocrBlock.LinkTo(florenceBlock, linkOptions);
        florenceBlock.LinkTo(postProcessBlock, linkOptions);

        await foreach (var page in pages)
            await classifyBlock.SendAsync(page);

        classifyBlock.Complete();
        await postProcessBlock.Completion;

        return results;
    }

    private async Task<PipelineData> ClassifyLayoutAsync(LayoutImage image)
    {
        _log($"[Page {image.Page.PageNumber}] Layout classification...");
        List<DetectionResult> raw = [];
        try
        {
            var detections = await _layoutClassifier.ClassifyAsync(image);
            raw = [.. detections];
        }
        catch (Exception ex)
        {
            _log($"[Page {image.Page.PageNumber}] Classification ERROR: {ex.GetType().Name}: {ex.Message}");
        }

        var adapter = CoordinateAdapter.ForImagePixels(image, _classifierOptions);
        bool isPdf = image.Page.PdfPageWidthPt > 0;

        var imageAll = adapter.MapToImageSpace(raw);
        var pdfAll   = isPdf ? adapter.MapToPdfSpace(raw, image.Page.PdfPageWidthPt, image.Page.PdfPageHeightPt) : [];

        // Build non-table lists, keeping image- and PDF-space detections index-aligned (pdfAll mirrors
        // imageAll element-for-element). The aspect-ratio decision is made once on the image-space box
        // and applied to both lists, dropping tall/narrow vertical-text regions before any OCR runs.
        var imageNonTable = new List<DetectionResult>();
        var pdfNonTable   = new List<DetectionResult>();
        int droppedVertical = 0;
        for (int i = 0; i < imageAll.Count; i++)
        {
            var det = imageAll[i];
            if (IsTableLabel(det.Label)) continue;
            if (_detectionFilter != null && !_detectionFilter.Keep(det)) { droppedVertical++; continue; }
            imageNonTable.Add(det);
            if (isPdf) pdfNonTable.Add(pdfAll[i]);
        }
        var imageTables = imageAll.Where(d => IsTableLabel(d.Label)).ToList();

        _log($"[Page {image.Page.PageNumber}] Found {imageAll.Count} regions" +
             (imageTables.Count > 0 ? $" ({imageTables.Count} tables)" : "") +
             (droppedVertical > 0 ? $", dropped {droppedVertical} vertical" : ""));

        return new PipelineData
        {
            Image = image,
            ImageDetections = imageNonTable,
            PdfDetections = pdfNonTable,
            TableDetections = imageTables,
            IsPdf = isPdf,
            PendingIndices = [.. Enumerable.Range(0, imageNonTable.Count)]
        };
    }

    private async Task<PipelineData> ExtractTablesAsync(PipelineData data)
    {
        if (data.TableDetections.Count == 0)
            return data;

        _log($"[Page {data.Image.Page.PageNumber}] Extracting {data.TableDetections.Count} table(s)...");
        try
        {
            var results = await _tableExtractor.ReadAsync(data.Image, data.TableDetections);
            data.FinalResults.AddRange(results);
            _log($"[Page {data.Image.Page.PageNumber}] Tables done: {results.Count} block(s)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Table extraction failed: {ex.Message}");
        }
        return data;
    }

    private async Task<PipelineData> ExtractManualAsync(PipelineData data)
    {
        if (!data.IsPdf || data.PdfDetections.Count == 0 || data.PendingIndices.Count == 0)
            return data;

        _log($"[Page {data.Image.Page.PageNumber}] L1 PdfPig: {data.PendingIndices.Count} region(s)...");
        try
        {
            var pendingDets = FilterPending(data.PdfDetections, data.PendingIndices);
            var results = await _manualExtractor.ReadAsync(data.Image, pendingDets);
            if (results.Count == 0) return data;

            int resolved = 0;
            foreach (int idx in data.PendingIndices.ToList())
            {
                var det = data.PdfDetections[idx];
                var blocks = results.Where(b => HasSignificantOverlap(det.BoundingBox, b.Box)).ToList();
                if (blocks.Count == 0) continue;

                double avgConf = blocks.Average(b => b.Confidence);
                if (avgConf >= ValidBlockConfidence)
                {
                    data.FinalResults.AddRange(blocks);
                    data.PendingIndices.Remove(idx);
                    resolved++;
                }
            }
            _log($"[Page {data.Image.Page.PageNumber}] L1 done: {resolved} resolved, {data.PendingIndices.Count} pending");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manual extraction failed: {ex.Message}");
        }
        return data;
    }

    private async Task<PipelineData> ExtractOcrAsync(PipelineData data)
    {
        if (data.PendingIndices.Count == 0 || data.ImageDetections.Count == 0)
            return data;

        _log($"[Page {data.Image.Page.PageNumber}] L2 PaddleOCR: {data.PendingIndices.Count} region(s)...");
        try
        {
            var pendingDets = FilterPending(data.ImageDetections, data.PendingIndices);
            var results = await _ocrExtractor.ReadAsync(data.Image, pendingDets);
            if (results.Count == 0) return data;

            // OCR splits a detection into paragraph blocks whose boxes are sub-regions of the detection
            // (page-space). Overlapping YOLO detections would otherwise each claim the same block, so
            // assign every block to exactly one pending detection — the one it overlaps most — and
            // resolve each detection from its own assigned blocks. This prevents duplicate blocks.
            var blocksByDet = new Dictionary<int, List<TextBlock>>();
            foreach (var block in results)
            {
                int bestIdx = -1;
                double bestOverlap = 0;
                foreach (int idx in data.PendingIndices)
                {
                    double ov = OverlapFraction(data.ImageDetections[idx].BoundingBox, block.Box);
                    if (ov >= 0.5 && ov > bestOverlap) { bestOverlap = ov; bestIdx = idx; }
                }
                if (bestIdx < 0) continue;
                (blocksByDet.TryGetValue(bestIdx, out var bucket) ? bucket : blocksByDet[bestIdx] = []).Add(block);
            }

            int resolved = 0;
            foreach (var (idx, blocks) in blocksByDet)
            {
                double avgConf = blocks.Average(b => b.Confidence);
                if (avgConf >= ValidBlockConfidence)
                {
                    data.FinalResults.AddRange(blocks);
                    data.PendingIndices.Remove(idx);
                    resolved++;
                }
            }
            _log($"[Page {data.Image.Page.PageNumber}] L2 done: {resolved} resolved, {data.PendingIndices.Count} pending");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR extraction failed: {ex.Message}");
        }
        return data;
    }

    private async Task<PipelineData> ExtractFlorenceAsync(PipelineData data)
    {
        if (_florenceExtractor == null || data.PendingIndices.Count == 0 || data.ImageDetections.Count == 0)
            return data;

        _log($"[Page {data.Image.Page.PageNumber}] L4 Florence: {data.PendingIndices.Count} region(s)...");
        try
        {
            var pendingDets = FilterPending(data.ImageDetections, data.PendingIndices);
            var results = await _florenceExtractor!.ReadAsync(data.Image, pendingDets);
            data.FinalResults.AddRange(results);
            data.PendingIndices.Clear();
            _log($"[Page {data.Image.Page.PageNumber}] L4 done: {results.Count} block(s)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Florence extraction failed: {ex.Message}");
        }
        return data;
    }

    private async Task PostProcessAsync(PipelineData data, List<TextBlock> results, object resultsLock)
    {
        // Terminal block: dispose the page (rendered SKImage + PDF stream) once it is fully processed,
        // on every path including the empty-result early return. Otherwise pages accumulate in the
        // dataflow buffers and pin tens of MB each until GC.
        try
        {
            if (data.FinalResults.Count == 0) return;

            var sorted = await _sortingService.SortAsync(data.FinalResults);
            var merged = await _mergingService.MergeAsync(sorted);

            int count;
            lock (resultsLock)
            {
                results.AddRange(merged);
                count = merged.Count();
            }
            _log($"[Page {data.Image.Page.PageNumber}] Done: {count} final block(s)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Post-processing failed: {ex.Message}");
        }
        finally
        {
            data.Image.Dispose();
        }
    }

    private static IReadOnlyList<DetectionResult> FilterPending(
        IReadOnlyList<DetectionResult> all, HashSet<int> pending) =>
        [.. pending.OrderBy(i => i).Select(i => all[i])];

    private static bool IsTableLabel(string? label) =>
        string.Equals(label, TableLabel, StringComparison.OrdinalIgnoreCase);

    // Checks if ≥50% of block's area is within det region (used by L1 where Box ≠ det.BoundingBox)
    private static bool HasSignificantOverlap(BoundingBox det, BoundingBox block)
        => OverlapFraction(det, block) >= 0.5;

    // Fraction of `block`'s area contained in `det` (0..1).
    private static double OverlapFraction(BoundingBox det, BoundingBox block)
    {
        double ix1 = Math.Max(det.X, block.X);
        double iy1 = Math.Max(det.Y, block.Y);
        double ix2 = Math.Min(det.X + det.Width, block.X + block.Width);
        double iy2 = Math.Min(det.Y + det.Height, block.Y + block.Height);
        if (ix2 <= ix1 || iy2 <= iy1) return 0;
        double area = block.Width * block.Height;
        return area > 0 ? (ix2 - ix1) * (iy2 - iy1) / area : 0;
    }

    private static async IAsyncEnumerable<LayoutImage> FeedSingle(LayoutImage image)
    {
        yield return image;
        await Task.CompletedTask;
    }

    public class PipelineData
    {
        public LayoutImage Image { get; set; } = new();
        public IReadOnlyList<DetectionResult> ImageDetections { get; set; } = [];
        public IReadOnlyList<DetectionResult> PdfDetections { get; set; } = [];
        public IReadOnlyList<DetectionResult> TableDetections { get; set; } = [];
        public bool IsPdf { get; set; }
        public HashSet<int> PendingIndices { get; set; } = [];
        public List<TextBlock> FinalResults { get; set; } = [];
    }
}
