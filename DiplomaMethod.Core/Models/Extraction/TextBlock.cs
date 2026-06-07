using DiplomaMethod.Core.Models.LayoutClassification;

namespace DiplomaMethod.Core.Models.Extraction;

public record TextBlock
{
    public List<TextLine> Lines { get; set; } = [];
    public BoundingBox Box { get; set; }
    public required string Label { get; set; }
    public double Confidence { get; set; }

    // Lines ending with '-' are hyphenated word-wraps — join directly; others get a space.
    public string Accumulate()
    {
        if (Lines.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Lines.Count; i++)
        {
            var text = Lines[i].Text;
            sb.Append(text);
            if (i < Lines.Count - 1 && !text.EndsWith('-'))
                sb.Append(' ');
        }
        return sb.ToString();
    }
    
}
