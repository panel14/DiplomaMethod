using DiplomaMethod.Application.Classifiers;
using DiplomaMethod.Application.DocumentReader;
using DiplomaMethod.Application.Extractors.Direct;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Base;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Strong;
using DiplomaMethod.Application.Extractors.Direct.Heuristics.Threshold;
using DiplomaMethod.Application.Extractors.Fallback;
using DiplomaMethod.Application.Extractors.Fallback.Florence;
using DiplomaMethod.Application.Extractors.Ocr;
using DiplomaMethod.Application.Extractors.Ocr.Heuristics;
using DiplomaMethod.Application.Extractors.Ocr.Postprocessors;
using DiplomaMethod.Application.Extractors.Ocr.Recognizers;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Line;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Window;
using DiplomaMethod.Application.Extractors.Ocr.Splitters.Word;
using DiplomaMethod.Application.Extractors.Table;
using DiplomaMethod.Application.Filters;
using DiplomaMethod.Application.Pipeline;
using DiplomaMethod.Application.Processors;
using DiplomaMethod.Core.Options;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: DiplomaMethod <file1> [file2] [file3] ...");
    return 1;
}

var inputFiles = args.Where(File.Exists).ToList();
var missing    = args.Where(a => !File.Exists(a)).ToList();

foreach (var m in missing)
    Console.Error.WriteLine($"[WARN] File not found, skipping: {m}");

if (inputFiles.Count == 0)
{
    Console.Error.WriteLine("No valid input files.");
    return 1;
}

var cfg = LoadSettings();

var totalSw = Stopwatch.StartNew();

Log("=== Pipeline initialization ===");

IStringHeuristic[] ocrHeuristics =
[
    new PrintableRatioHeuristic(),
    new UpperSymbolsStringHeuristic(),
    new RepeatedCharsHeuristic()
];

var textValidator = new TextValidator(
    [new ControlCharHeuristic(0.05), new WordOverlapHeuristic()],
    [new LineCountHeuristic(), new UpperSymbolsThreshold()]);

var paddleOptions = new PaddleOcrRecognizerV3Options
{
    UseGpu = cfg.Paddle.UseGpu,
    GpuId = cfg.Paddle.GpuId,
    GpuInitialMemoryMb = cfg.Paddle.GpuInitialMemoryMb
};

var paddleV5Options = new PaddleOcrRecognizerV5Options
{
    ModelPath               = ResolvePath(cfg.PaddleV5.ModelPath),
    DictPath                = ResolvePath(cfg.PaddleV5.DictPath),
    UseGpu                  = cfg.PaddleV5.UseGpu,
    GpuId                   = cfg.PaddleV5.GpuId,
    UseSpaceChar            = cfg.PaddleV5.UseSpaceChar,
    WordSpaceBlankThreshold = cfg.PaddleV5.WordSpaceBlankThreshold,
};

var tessOptions = new TesseractOptions
{
    TessDataPath = ResolvePath(cfg.Tesseract.TessDataPath),
    Language = cfg.Tesseract.Language,
    PageSegMode = cfg.Tesseract.PageSegMode,
    UpscaleMinHeight = cfg.Tesseract.UpscaleMinHeight
};

var ocrOptions = new OcrOptions
{
    MinPaddleScore   = cfg.Ocr.MinPaddleScore,
    WordSegmentation = cfg.Ocr.WordSegmentation,
};

var stepSw = Stopwatch.StartNew();

FlorenceEngine? florenceEngine = null;
FlorenceBlockExtractor? florenceExtractor = null;
if (cfg.Florence.Enabled)
{
    Log("Loading Florence-2 model...");
    stepSw.Restart();
    florenceEngine = await FlorenceEngine.CreateAsync(new FlorenceOcrOptions
    {
        ModelPath = ResolvePath(cfg.Florence.ModelPath),
        AutoDownload = cfg.Florence.AutoDownload
    });
    florenceExtractor = new FlorenceBlockExtractor(florenceEngine);
    Log($"Florence-2 ready ({stepSw.Elapsed.TotalSeconds:F1}s)");
}
else
{
    Log("Florence-2 (VLM layer) disabled by configuration — skipping.");
}

Log("Loading YOLO layout classifier...");
stepSw.Restart();
using var layoutClassifier = new YoloLayoutClassifier(ResolvePath(cfg.Classifier.ModelPath));
Log($"YOLO ready ({stepSw.Elapsed.TotalSeconds:F1}s)");

Log("Loading PaddleOCR...");
stepSw.Restart();
using var ocrService = new OcrService(ocrOptions, paddleV5Options,
    lineSplitter:      new LinearSegmentLinkingSplitter(topPaddingPx: 4),
    wordSplitter:      new CcaWordSplitter(),
    lineHeuristics:    ocrHeuristics,
    linePostprocessor: new MedianHeightLineFilter(),
    windowSplitter:    new LineWindowSplitter());
Log($"PaddleOCR ready ({stepSw.Elapsed.TotalSeconds:F1}s)");

Log("Loading Paddle table extractor...");
stepSw.Restart();
using var tableExtractor = new PaddleTableExtractor(paddleOptions);
Log($"Paddle table ready ({stepSw.Elapsed.TotalSeconds:F1}s)");

Log("Loading Tesseract...");
stepSw.Restart();
using var tesseractRecognizer = new TesseractOcrRecognizer(tessOptions);
Log($"Tesseract ready ({stepSw.Elapsed.TotalSeconds:F1}s)");

var pipeline = new TextExtractionPipeline(
    documentReader:     new DocumentReader(),
    layoutClassifier:   layoutClassifier,
    manualExtractor:    new PdfPigExtractorService(textValidator),
    ocrExtractor:       ocrService,
    tesseractExtractor: new TesseractBlockExtractor(tesseractRecognizer, ocrHeuristics),
    florenceExtractor:  florenceExtractor,
    tableExtractor:     tableExtractor,
    sortingService:     new GeometrySortingService(),
    mergingService:     new ParagraphMergingService(),
    classifierOptions:  new ClassifierOptions(),
    detectionFilter:    new AspectRatioDetectionFilter(),
    logger:             Log);

Log($"=== Pipeline ready ({totalSw.Elapsed.TotalSeconds:F1}s total) ===\n");

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

foreach (var inputFile in inputFiles)
{
    Log($"--- Processing: {Path.GetFileName(inputFile)} ---");
    var fileSw = Stopwatch.StartNew();

    var blocks = await pipeline.ProcessDocumentFromFileAsync(inputFile);
    fileSw.Stop();

    Log($"Extracted {blocks.Count} blocks in {fileSw.Elapsed.TotalSeconds:F1}s");

    string outputFile = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(inputFile))!,
        Path.GetFileNameWithoutExtension(inputFile) + "_extracted.json");

    var output = new ExtractionOutput(
        InputFile:      Path.GetFullPath(inputFile),
        ProcessedAt:    DateTime.UtcNow.ToString("O"),
        ElapsedSeconds: Math.Round(fileSw.Elapsed.TotalSeconds, 2),
        BlockCount:     blocks.Count,
        Blocks:         [.. blocks.Select(b => new BlockOutput(
            Label:      b.Label,
            Confidence: Math.Round(b.Confidence, 4),
            Box:        new BoxOutput(b.Box.X, b.Box.Y, b.Box.Width, b.Box.Height),
            Lines:      [.. b.Lines.Select(l => new LineOutput(
                l.Text,
                new BoxOutput(l.Box.X, l.Box.Y, l.Box.Width, l.Box.Height)))]))]);

    await File.WriteAllTextAsync(outputFile, JsonSerializer.Serialize(output, jsonOptions));
    Log($"Output: {outputFile}\n");
}

florenceEngine?.Dispose();

Log($"=== All done ({totalSw.Elapsed.TotalSeconds:F1}s total) ===");
return 0;

// ─── Helpers ──────────────────────────────────────────────────────────────────

static void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

static AppSettings LoadSettings()
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"[WARN] appsettings.json not found at {configPath}, using defaults");
        return new AppSettings();
    }

    var json = File.ReadAllText(configPath);
    return JsonSerializer.Deserialize<AppSettings>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? new AppSettings();
}

record AppSettings
{
    public ClassifierSettings  Classifier { get; init; } = new();
    public TesseractSettings   Tesseract  { get; init; } = new();
    public FlorenceSettings    Florence   { get; init; } = new();
    public PaddleSettings      Paddle     { get; init; } = new();
    public PaddleV5Settings    PaddleV5   { get; init; } = new();
    public OcrSettings         Ocr        { get; init; } = new();
}

record ClassifierSettings
{
    public string ModelPath { get; init; } = "./models/layout/model.onnx";
}

record TesseractSettings
{
    public string TessDataPath     { get; init; } = "./tessdata";
    public string Language         { get; init; } = "rus+eng";
    public int    PageSegMode      { get; init; } = 6;
    public int    UpscaleMinHeight { get; init; } = 32;
}

record FlorenceSettings
{
    public bool   Enabled      { get; init; } = true;
    public string ModelPath    { get; init; } = "./models/florence2";
    public bool   AutoDownload { get; init; } = true;
}

record PaddleSettings
{
    public bool UseGpu             { get; init; } = false;
    public int  GpuId              { get; init; } = 0;
    public int  GpuInitialMemoryMb { get; init; } = 500;
}

record PaddleV5Settings
{
    public string ModelPath               { get; init; } = "./models/eslav/rec.onnx";
    public string DictPath                { get; init; } = "./models/eslav/dict.txt";
    public bool   UseGpu                  { get; init; } = false;
    public int    GpuId                   { get; init; } = 0;
    public bool   UseSpaceChar            { get; init; } = false;
    public int    WordSpaceBlankThreshold { get; init; } = 0;
}

record OcrSettings
{
    public float                MinPaddleScore   { get; init; } = 0.8f;
    public WordSegmentationMode WordSegmentation { get; init; } = WordSegmentationMode.None;
}

record ExtractionOutput(
    string        InputFile,
    string        ProcessedAt,
    double        ElapsedSeconds,
    int           BlockCount,
    BlockOutput[] Blocks);

record BlockOutput(
    string       Label,
    double       Confidence,
    BoxOutput    Box,
    LineOutput[] Lines);

record LineOutput(string Text, BoxOutput Box);

record BoxOutput(double X, double Y, double Width, double Height);
