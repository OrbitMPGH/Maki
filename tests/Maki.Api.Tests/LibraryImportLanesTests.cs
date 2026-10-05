using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// Imports run in parallel, but two items naming the same series or the same folder must run one
/// after another, or both could add the series or both could adopt the folder.
/// </summary>
public class LibraryImportLanesTests
{
    [Fact]
    public void Unrelated_items_each_get_their_own_lane()
    {
        var lanes = LibraryImportService.ImportLanes(
        [
            new ImportRequestItem("A", "1"),
            new ImportRequestItem("B", "2"),
            new ImportRequestItem("C", "3"),
        ]);

        Assert.Equal([[0], [1], [2]], lanes);
    }

    [Fact]
    public void Same_series_shares_a_lane_in_request_order()
    {
        var lanes = LibraryImportService.ImportLanes(
        [
            new ImportRequestItem("A", "1"),
            new ImportRequestItem("B", "2"),
            new ImportRequestItem("C", "1"),
        ]);

        Assert.Equal([[0, 2], [1]], lanes);
    }

    [Fact]
    public void Same_folder_shares_a_lane_and_chains_through_series()
    {
        // 0 and 1 share a series, 1 and 2 share a folder: all three are one lane.
        var lanes = LibraryImportService.ImportLanes(
        [
            new ImportRequestItem("A", "1"),
            new ImportRequestItem("B", "1"),
            new ImportRequestItem("B", "2"),
            new ImportRequestItem("D", "3"),
        ]);

        Assert.Contains(lanes, l => l.SequenceEqual([3]));
        Assert.Contains(lanes, l => l.Count == 3 && l.Contains(0) && l.Contains(1) && l.Contains(2) && l[0] == 0);
    }
}
