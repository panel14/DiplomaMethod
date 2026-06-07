using System;

namespace DiplomaMethod.Core.Options;

public class ClassifierOptions
{
    public int TargetWidth { get; init; } = 640;
    public int TargetHeight { get; init; } = 640;
    public int ChannelCount { get; init; } = 3;
    // DocLayNet label order (0-indexed, matches yolov8x-doclaynet)
    public static string[] ClassLabels { get; } =
    [
        "Caption", "Footnote", "Formula", "List",
        "Page Footer", "Page Header", "Figure", "Section Header",
        "Table", "Text", "Title"
    ];
}
