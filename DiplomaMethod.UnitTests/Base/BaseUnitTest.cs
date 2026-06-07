using DiplomaMethod.Core.Options;
using Microsoft.Extensions.Configuration;

namespace DiplomaMethod.UnitTests.Base;

public abstract class BaseUnitTest
{
    private IConfiguration? _configuration;

    protected IConfiguration Configuration =>
        _configuration ??= new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .Build();

    public abstract string BaseConfigSection { get; set; }

    [SetUp]
    public virtual void SetUp() { }

    protected OcrOptions ReadOcrOptions(string section = "Ocr")
    {
        var s = section;
        return new OcrOptions
        {
            MinPaddleScore  = ParseFloat(s, "MinPaddleScore", 0.87f),
            WordSegmentation = ParseEnum(s, "WordSegmentation", WordSegmentationMode.None),
            Preprocess       = ReadPreprocessOptions($"{s}:Preprocess"),
        };
    }

    private OcrPreprocessOptions ReadPreprocessOptions(string s) => new()
    {
        Enabled                 = ParseBool(s, "Enabled",                 false),
        Deskew                  = ParseBool(s, "Deskew",                  true),
        MinHeightForDeskew      = ParseInt(s,  "MinHeightForDeskew",      60),
        MaxDeskewAngle          = ParseFloat(s, "MaxDeskewAngle",         5.0f),
        DeskewStep              = ParseFloat(s, "DeskewStep",             0.5f),
        AdaptiveBinarize        = ParseBool(s, "AdaptiveBinarize",        true),
        AdaptiveBlockSize       = ParseInt(s,  "AdaptiveBlockSize",       31),
        AdaptiveC               = ParseDouble(s, "AdaptiveC",             10.0),
        LineUpscaleMinHeight    = ParseInt(s,  "LineUpscaleMinHeight",    32),
        LineUpscaleTargetHeight = ParseInt(s,  "LineUpscaleTargetHeight", 64),
    };

    private bool   ParseBool  (string s, string k, bool   d) => bool.TryParse(Configuration[$"{s}:{k}"],   out var v) ? v : d;
    private int    ParseInt   (string s, string k, int    d) => int.TryParse(Configuration[$"{s}:{k}"],    out var v) ? v : d;
    private float  ParseFloat (string s, string k, float  d) => float.TryParse(Configuration[$"{s}:{k}"],  System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    private double ParseDouble(string s, string k, double d) => double.TryParse(Configuration[$"{s}:{k}"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    private T      ParseEnum<T>(string s, string k, T     d) where T : struct, Enum
        => Enum.TryParse<T>(Configuration[$"{s}:{k}"], ignoreCase: true, out var v) ? v : d;

    protected string GetConfigValue(string key)
    {
        var configPath = Configuration[$"{BaseConfigSection}:{key}"];

        if (string.IsNullOrEmpty(configPath))
        {
            configPath = Path.Combine(BaseConfigSection, key);
        }

        if (!Path.IsPathRooted(configPath))
        {
            var testAssemblyPath = Path.GetDirectoryName(typeof(BaseUnitTest).Assembly.Location) ?? Directory.GetCurrentDirectory();
            configPath = Path.Combine(testAssemblyPath, configPath);
        }

        return configPath;
    }
}
