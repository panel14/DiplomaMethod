using System.Diagnostics;

namespace DiplomaMethod.Application.Diagnostics;

public static class OcrProfiler
{
    public static bool Enabled;

    private static long _inferTicks;
    private static long _batchBuildTicks;
    private static long _windowSplitTicks;
    private static long _lslTicks;
    private static long _paddleCalls;
    private static long _windowsTotal;
    private static long _windowWidthSum;

    public static void Reset()
    {
        _inferTicks = _batchBuildTicks = _windowSplitTicks = _lslTicks = 0;
        _paddleCalls = _windowsTotal = _windowWidthSum = 0;
    }

    public static void AddInfer(long ticks) => Interlocked.Add(ref _inferTicks, ticks);
    public static void AddBatchBuild(long ticks) => Interlocked.Add(ref _batchBuildTicks, ticks);
    public static void AddWindowSplit(long ticks) => Interlocked.Add(ref _windowSplitTicks, ticks);
    public static void AddLsl(long ticks) => Interlocked.Add(ref _lslTicks, ticks);

    public static void AddPaddleBatch(int imageCount, long widthSum)
    {
        Interlocked.Increment(ref _paddleCalls);
        Interlocked.Add(ref _windowsTotal, imageCount);
        Interlocked.Add(ref _windowWidthSum, widthSum);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static string Report(TimeSpan total)
    {
        double totalMs = total.TotalMilliseconds;
        double Pct(long t) => totalMs > 0 ? Ms(t) / totalMs * 100.0 : 0.0;
        double avgW = _windowsTotal > 0 ? (double)_windowWidthSum / _windowsTotal : 0.0;

        return
            "=== OCR profile ===\n" +
            $"total wall:                {total.TotalSeconds:F2}s\n" +
            $"inference (_session.Run):  {Ms(_inferTicks):F0}ms ({Pct(_inferTicks):F0}%)\n" +
            $"batch-build (tensor prep): {Ms(_batchBuildTicks):F0}ms ({Pct(_batchBuildTicks):F0}%)\n" +
            $"window-split:              {Ms(_windowSplitTicks):F0}ms ({Pct(_windowSplitTicks):F0}%)\n" +
            $"lsl (line split):          {Ms(_lslTicks):F0}ms ({Pct(_lslTicks):F0}%)\n" +
            $"paddle calls: {_paddleCalls} | windows: {_windowsTotal} | " +
            $"avg window width: {avgW:F0}px (at h=48)";
    }
}
