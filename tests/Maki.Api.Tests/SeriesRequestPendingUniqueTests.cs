using System.Data.Common;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Two identical pending requests used to both save: <see cref="SeriesRequestSubmitter.SubmitAsync"/>
/// only checked for a duplicate before inserting, which two submits arriving at once can both pass.
/// The real guard is the partial unique index the <c>SeriesRequestPendingUnique</c> migration adds,
/// exercised here against a genuinely migrated database, since <see cref="TestDb"/>'s
/// <c>EnsureCreated()</c> never runs a single migration and would not create it.
/// </summary>
public class SeriesRequestPendingUniqueTests : IDisposable
{
    /// <summary>The last migration before this one.</summary>
    private const string BeforeUniqueIndex = "20260927214918_DownloadQueueActiveChapter";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public SeriesRequestPendingUniqueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private MakiDbContext NewContext() => new(_options);

    private void MigrateTo(string target)
    {
        using var db = NewContext();
        db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(target);
    }

    private void MigrateToHead()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    /// <summary>Every <c>SeriesRequests</c> row needs an owning user; the placeholder admin is Id 1.</summary>
    private int SeedUser(string userName)
    {
        using var db = NewContext();
        var user = new Maki.Data.Identity.MakiUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            AllRootFolders = true,
            MaxContentRating = "erotica",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>
    /// The schema for <c>SeriesRequests</c> is unchanged by this migration (it only adds an index),
    /// so seeding through the current entity model against the pre-migration schema is safe: unlike
    /// a migration that adds columns, there is nothing here the historical table lacks.
    /// </summary>
    private int SeedPendingRequest(int userId, string metadataProviderId, DateTime created)
    {
        using var db = NewContext();
        var request = new SeriesRequest
        {
            UserId = userId,
            Kind = SeriesRequestKind.NewSeries,
            Status = SeriesRequestStatus.Pending,
            MetadataProviderId = metadataProviderId,
            Title = $"Series {metadataProviderId}",
            Created = created,
        };
        db.SeriesRequests.Add(request);
        db.SaveChanges();
        return request.Id;
    }

    private T Scalar<T>(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T))!;
    }

    [Fact]
    public void Migrating_dedupes_existing_pending_duplicates_keeping_the_oldest()
    {
        MigrateTo(BeforeUniqueIndex);
        var secondUserId = SeedUser("reader2");
        var oldest = SeedPendingRequest(1, "1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = SeedPendingRequest(1, "1", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        // A different user asking for the same title is not a duplicate of theirs.
        var otherUser = SeedPendingRequest(secondUserId, "1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        MigrateToHead();

        using var db = NewContext();
        var remaining = db.SeriesRequests.IgnoreQueryFilters().Select(r => r.Id).OrderBy(id => id).ToList();
        Assert.Equal([oldest, otherUser], remaining);
        Assert.DoesNotContain(newer, remaining);
    }

    [Fact]
    public void Resolved_duplicates_survive_the_dedupe()
    {
        MigrateTo(BeforeUniqueIndex);
        using (var db = NewContext())
        {
            var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            db.SeriesRequests.AddRange(
                new SeriesRequest
                {
                    UserId = 1, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Rejected,
                    MetadataProviderId = "1", Title = "Series 1", Created = created,
                },
                new SeriesRequest
                {
                    UserId = 1, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Approved,
                    MetadataProviderId = "1", Title = "Series 1", Created = created,
                });
            db.SaveChanges();
        }

        MigrateToHead();

        using var check = NewContext();
        Assert.Equal(2, check.SeriesRequests.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void The_index_refuses_a_second_identical_pending_request_at_the_database_level()
    {
        MigrateToHead();
        SeedPendingRequest(1, "1", DateTime.UtcNow);

        var ex = Assert.Throws<DbUpdateException>(() => SeedPendingRequest(1, "1", DateTime.UtcNow));

        Assert.Equal(2067, Assert.IsType<SqliteException>(ex.InnerException).SqliteExtendedErrorCode);
    }

    [Fact]
    public void A_second_request_after_the_first_is_resolved_is_not_a_duplicate()
    {
        MigrateToHead();
        var firstId = SeedPendingRequest(1, "1", DateTime.UtcNow);
        using (var db = NewContext())
        {
            db.SeriesRequests.IgnoreQueryFilters().Single(r => r.Id == firstId).Status = SeriesRequestStatus.Rejected;
            db.SaveChanges();
        }

        SeedPendingRequest(1, "1", DateTime.UtcNow);

        using var check = NewContext();
        Assert.Equal(2, check.SeriesRequests.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void Index_exists_and_is_unique_and_partial()
    {
        MigrateToHead();

        var sql = Scalar<string>(
            """SELECT "sql" FROM sqlite_master WHERE name = 'IX_SeriesRequests_Pending_Identity';""");
        Assert.Contains("UNIQUE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The end-to-end path: two submits racing each other both pass <c>SubmitAsync</c>'s own
    /// duplicate check before either has saved, so only the database index actually stops the second
    /// one, which <see cref="SeriesRequestSubmitter"/> then has to read back as
    /// <see cref="SeriesRequestSubmitError.AlreadyPending"/> rather than letting the exception escape.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_identical_submits_yield_one_row_and_the_second_reads_as_already_pending()
    {
        MigrateToHead();
        var metadata = new FakeMetadataProvider();
        var events = new EventBroadcaster(new NoopHubContext(), null!);
        var inbox = new RecordingInbox();
        var notifications = new RecordingNotifications();

        var competitor = new CompetingInsert();
        var options = new DbContextOptionsBuilder<MakiDbContext>(_options).AddInterceptors(competitor).Options;
        var db = new MakiDbContext(options);
        var submitter = new SeriesRequestSubmitter(
            db, [metadata], events, inbox, notifications,
            new TestUserLocaleResolver(), new TestLocalizer(), NullLogger<SeriesRequestSubmitter>.Instance);

        var request = new SeriesRequest
        {
            UserId = 1, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Pending,
            MetadataProviderId = "1", Title = "Series 1", Created = DateTime.UtcNow,
        };

        var result = await submitter.SubmitAsync(request, "reader", default);

        Assert.True(competitor.Fired);
        Assert.Equal(SeriesRequestSubmitError.AlreadyPending, result.Error);
        using var check = NewContext();
        Assert.Single(check.SeriesRequests.IgnoreQueryFilters());
    }

    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = $"Series {providerId}", MangaBakaId = int.Parse(providerId),
            });
    }

    /// <summary>Lands a second, identical pending request just before this submit's own insert commits.</summary>
    private sealed class CompetingInsert : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("INSERT INTO \"SeriesRequests\""))
            {
                Fired = true;
                await using var other = command.Connection!.CreateCommand();
                other.Transaction = command.Transaction;
                other.CommandText =
                    """
                    INSERT INTO "SeriesRequests" ("UserId", "Kind", "Status", "MetadataProviderId", "Title", "Created")
                    VALUES (1, 0, 0, '1', 'Series 1', '2026-01-01 00:00:00');
                    """;
                await other.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }
}
