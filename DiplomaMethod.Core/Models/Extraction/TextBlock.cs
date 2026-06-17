using DiplomaMethod.Core.Models.LayoutClassification;

namespace DiplomaMethod.Core.Models.Extraction;

public record TextBlock
{
    public List<TextLine> Lines { get; set; } = [];
    public BoundingBox Box { get; set; }
    public required string Label { get; set; }
    public double Confidence { get; set; }

    // A line ending with '-' is a hyphenated word-wrap: drop the hyphen and join the two halves
    // directly (no space). Other lines are joined with a single space.
    public string Accumulate()
    {
        if (Lines.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Lines.Count; i++)
        {
            var text = Lines[i].Text;
            bool notLast = i < Lines.Count - 1;

            if (notLast && text.EndsWith('-'))
            {
                sb.Append(text, 0, text.Length - 1); // strip the wrap hyphen, no separator
            }
            else
            {
                sb.Append(text);
                if (notLast) sb.Append(' ');
            }
        }
        return sb.ToString();
    }
    
}
