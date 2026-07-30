using Maki.Api.Auth;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// A throwaway <see cref="MakiDbContext"/> over an in-memory SQLite database.
/// The connection is kept open for the fixture's lifetime so the schema survives
/// between contexts; each <see cref="NewContext"/> is a fresh unit-of-work over the
/// same data, which is how the real per-scope DbContext usage behaves.
/// </summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public MakiDbContext NewContext() => new(_options);

    /// <summary>A scope factory whose scopes each resolve a fresh context over this same DB.</summary>
    public IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Seeds a user and returns its id.
    /// <para>
    /// Needed explicitly because this fixture builds the schema with <c>EnsureCreated()</c>, which
    /// runs no migrations — so the placeholder admin that the multi-user migration inserts does not
    /// exist here. <see cref="MultiUserMigrationTests"/> is what covers that row.
    /// </para>
    /// </summary>
    public int SeedUser(
        string userName = "reader",
        MakiPermission permissions = MakiPermission.Admin,
        bool allRootFolders = true,
        string maxContentRating = "erotica",
        Action<MakiUser>? configure = null)
    {
        using var db = NewContext();
        var user = new MakiUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Permissions = permissions,
            AllRootFolders = allRootFolders,
            MaxContentRating = maxContentRating,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        configure?.Invoke(user);

        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>
    /// Seeds an API key for a user and returns the plaintext secret — the only place it exists, since
    /// the row stores nothing but its digest.
    /// </summary>
    public string SeedApiKey(
        int userId,
        UserApiKeyScope scope = UserApiKeyScope.Full,
        bool revoked = false)
    {
        var secret = ApiKeyCrypto.Generate();

        using var db = NewContext();
        db.UserApiKeys.Add(new UserApiKey
        {
            UserId = userId,
            Name = $"{scope} key",
            KeyHash = ApiKeyCrypto.Hash(secret),
            Prefix = ApiKeyCrypto.Prefix(secret),
            Scope = scope,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RevokedAt = revoked ? new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) : null
        });
        db.SaveChanges();

        return secret;
    }

    /// <summary>Replaces the AppConfig table with exactly these entries.</summary>
    public void SetConfig(params (string Key, string Value)[] entries)
    {
        using var db = NewContext();
        db.AppConfig.RemoveRange(db.AppConfig);
        db.AppConfig.AddRange(entries.Select(e => new AppConfigEntry { Key = e.Key, Value = e.Value }));
        db.SaveChanges();
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
