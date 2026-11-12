using Maki.Core.Entities;

namespace Maki.Api.Services;

/// <summary>
/// When a read mark reaches the end of a series. Shared by the progress writer, which sets
/// <c>ReadingState.Finished</c>, and the reader manifest, which tells the end screen ahead of the
/// write that finishing this chapter will do it.
/// </summary>
public static class SeriesFinishRule
{
    public static bool Closes(SeriesStatus? status, double mark, decimal? highestNumber) =>
        status == SeriesStatus.Completed && mark > 0 &&
        highestNumber is { } highest && highest > 0 && Math.Floor(mark) >= Math.Floor((double)highest);
}
