using DiplomaMethod.Core.Models.Extraction;
using DiplomaMethod.Core.Services;

namespace DiplomaMethod.Application.Processors;

public class GeometrySortingService : ISortingService
{
    public Task<IEnumerable<TextBlock>> SortAsync(IEnumerable<TextBlock> blocks)
    {
        var list = blocks.ToList();
        if (list.Count <= 1)
            return Task.FromResult<IEnumerable<TextBlock>>(list);

        return Task.FromResult<IEnumerable<TextBlock>>(SortByReadingOrder(list));
    }

    private static List<TextBlock> SortByReadingOrder(List<TextBlock> blocks)
    {
        double tolerance = blocks
            .Select(b => b.Box.Height)
            .OrderBy(h => h)
            .ElementAt(blocks.Count / 2) * 0.5;

        var byY = blocks.OrderBy(b => b.Box.Y).ToList();
        var used = new bool[byY.Count];
        var result = new List<TextBlock>(byY.Count);

        for (int i = 0; i < byY.Count; i++)
        {
            if (used[i]) continue;

            var band = new List<TextBlock> { byY[i] };
            used[i] = true;

            double bandTop = byY[i].Box.Y;
            double bandBottom = byY[i].Box.Y + byY[i].Box.Height;

            for (int j = i + 1; j < byY.Count; j++)
            {
                if (used[j]) continue;
                var b = byY[j];
                if (b.Box.Y > bandBottom + tolerance) break;

                double overlap = Math.Min(bandBottom, b.Box.Y + b.Box.Height) - Math.Max(bandTop, b.Box.Y);
                if (overlap > 0)
                {
                    band.Add(b);
                    used[j] = true;
                    bandBottom = Math.Max(bandBottom, b.Box.Y + b.Box.Height);
                }
            }

            band.Sort(static (a, b) => a.Box.X.CompareTo(b.Box.X));
            result.AddRange(band);
        }

        return result;
    }
}
