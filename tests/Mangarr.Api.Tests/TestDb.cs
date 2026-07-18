using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mangarr.Api.Tests;

/// <summary>
/// A throwaway <see cref="MangarrDbContext"/> over an in-memory SQLite database.
/// The connection is kept open for the fixture's lifetime so the schema survives
/// between contexts; each <see cref="NewContext"/> is a fresh unit-of-work over the
/// same data, which is how the real per-scope DbContext usage behaves.
/// </summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MangarrDbContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MangarrDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public MangarrDbContext NewContext() => new(_options);

    /// <summary>A scope factory whose scopes each resolve a fresh context over this same DB.</summary>
    public IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Seeds a series (with a backing root folder) and optional source mappings, returns its id.</summary>
    public int SeedSeries(
        string title = "Test Series",
        NewChapterMonitorMode monitor = NewChapterMonitorMode.All,
        string? originalTitle = null,
        string? mangaDexUuid = null,
        Action<Series>? configure = null,
        params SourceMapping[] mappings)
    {
        using var db = NewContext();
        var root = new RootFolder { Path = "/manga" };
        db.RootFolders.Add(root);
        db.SaveChanges();

        var series = new Series
        {
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            OriginalTitle = originalTitle,
            MangaDexUuid = mangaDexUuid,
            MonitorNewItems = monitor,
            RootFolderId = root.Id,
            FolderName = title,
            Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        series.SourceMappings.AddRange(mappings);
        configure?.Invoke(series);

        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    public void Dispose() => _connection.Dispose();
}
