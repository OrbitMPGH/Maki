using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// SQLite stores a DateTime without an offset marker, so a timestamp written as UTC used to read
/// back as Kind=Unspecified and be reinterpreted as local time by anything building a
/// DateTimeOffset from it. On a negative UTC offset that pushed QueuedAt into the future and left
/// torrent claiming with a window nothing could satisfy.
/// </summary>
public class UtcTimestampRoundTripTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private readonly int _seriesId;

    public UtcTimestampRoundTripTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        var folder = new RootFolder { Path = Path.GetTempPath() };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        var series = new Series
        {
            Title = "Timestamps",
            SortTitle = "timestamps",
            RootFolderId = folder.Id,
            FolderName = "Timestamps",
        };
        db.Series.Add(series);
        db.SaveChanges();
        _seriesId = series.Id;
    }

    private MakiDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task TimestampComesBackAsUtc()
    {
        var queuedAt = DateTime.UtcNow;

        await using (var db = NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = _seriesId,
                Protocol = AcquisitionProtocol.Torrent,
                QueuedAt = queuedAt,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var item = await db.DownloadQueue.SingleAsync();
            Assert.Equal(DateTimeKind.Utc, item.QueuedAt.Kind);
            Assert.Equal(
                new DateTimeOffset(queuedAt).ToUnixTimeSeconds(),
                new DateTimeOffset(item.QueuedAt).ToUnixTimeSeconds());
        }
    }

    [Fact]
    public async Task NullableTimestampComesBackAsUtc()
    {
        var completedAt = DateTime.UtcNow;

        await using (var db = NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = _seriesId,
                Protocol = AcquisitionProtocol.Torrent,
                CompletedAt = completedAt,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var item = await db.DownloadQueue.SingleAsync();
            Assert.Equal(DateTimeKind.Utc, item.CompletedAt!.Value.Kind);
        }
    }
}
