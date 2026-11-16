using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public partial class PageConversionPreviewService
{
    private const int RowSampleTries = 12;

    private const int RowSampleCount = 3;

    /// <summary>
    /// Up to three pages of one kind from <paramref name="fileIds"/>, one per series, the series
    /// holding the most bytes first. Colour looks in manhwa and manhua first, black-and-white in manga.
    /// </summary>
    public async Task<IReadOnlyList<PageConversionSample>> SamplesFromAsync(IReadOnlyList<int> fileIds, bool colour,
        CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return [];
        }

        var inRow = Candidates().Where(f => fileIds.Contains(f.Id));
        var likely = colour
            ? db.Series.IgnoreQueryFilters().Where(s => s.Type != null && ColourTypes.Contains(s.Type)).Select(s => s.Id)
            : db.Series.IgnoreQueryFilters().Where(s => s.Type == SeriesTypes.Manga).Select(s => s.Id);
        var files = await SpreadAsync(inRow.Where(f => likely.Contains(f.SeriesId)), new HashSet<int>(), RowSampleTries, ct);
        if (files.Count < RowSampleTries)
        {
            var seen = files.Select(f => f.SeriesId).ToHashSet();
            files.AddRange(await SpreadAsync(inRow, seen, RowSampleTries - files.Count, ct));
        }

        var found = new List<PageConversionSample>();
        foreach (var candidate in files)
        {
            if (found.Count >= RowSampleCount)
            {
                break;
            }

            if (await ProbeAsync(candidate, colour ? PageKind.Colour : PageKind.Grey, ct) is { } probe)
            {
                found.Add(await SampleAsync(candidate, probe, colour ? "colour" : "greyscale", ct));
            }
        }

        return found;
    }
}
