using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Maki.Data;

/// <summary>
/// Derives from <see cref="IdentityUserContext{TUser,TKey}"/> rather than the role-aware
/// <c>IdentityDbContext</c>: permissions are flag bits on <see cref="MakiUser.Permissions"/>, so
/// the Roles/UserRoles/RoleClaims tables would only ever be empty. This still gives the users,
/// claims, logins (the OIDC subject link) and tokens (TOTP recovery codes) tables, and
/// <c>AddEntityFrameworkStores</c> resolves the user-only store against it.
/// </summary>
public class MakiDbContext(DbContextOptions<MakiDbContext> options, DataScope? scope = null)
    : IdentityUserContext<MakiUser, int>(options)
{
    /// <summary>
    /// Drives the global query filters. Null means nothing registered one — design-time tooling and
    /// tests, which construct the context directly and get an unrestricted scope.
    /// </summary>
    private readonly DataScope _scope = scope ?? new DataScope();

    /// <summary>
    /// The scope this context is filtering by. Exposed so a background job can widen its own scope
    /// after resolving the context, and so tests can narrow one.
    /// </summary>
    public DataScope Scope => _scope;

    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<UserRootFolder> UserRootFolders => Set<UserRootFolder>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<UserSeriesState> UserSeriesStates => Set<UserSeriesState>();
    public DbSet<AuthEvent> AuthEvents => Set<AuthEvent>();

    public DbSet<Series> Series => Set<Series>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<ChapterFile> ChapterFiles => Set<ChapterFile>();
    public DbSet<SourceMapping> SourceMappings => Set<SourceMapping>();
    public DbSet<ChapterSourceLink> ChapterSourceLinks => Set<ChapterSourceLink>();
    public DbSet<DownloadQueueItem> DownloadQueue => Set<DownloadQueueItem>();
    public DbSet<RootFolder> RootFolders => Set<RootFolder>();
    public DbSet<NamingConfig> NamingConfigs => Set<NamingConfig>();
    public DbSet<AppConfigEntry> AppConfig => Set<AppConfigEntry>();
    public DbSet<ScrobbleToken> ScrobbleTokens => Set<ScrobbleToken>();
    public DbSet<ScrobbleMapping> ScrobbleMappings => Set<ScrobbleMapping>();
    public DbSet<ScrobbleSyncState> ScrobbleSyncStates => Set<ScrobbleSyncState>();
    public DbSet<SeriesScrobbleState> SeriesScrobbleStates => Set<SeriesScrobbleState>();
    public DbSet<ScrobbleUnmatched> ScrobbleUnmatched => Set<ScrobbleUnmatched>();
    public DbSet<ScrobbleLogEntry> ScrobbleLog => Set<ScrobbleLogEntry>();
    public DbSet<StatsEvent> StatsEvents => Set<StatsEvent>();
    public DbSet<ReadingState> ReadingStates => Set<ReadingState>();
    public DbSet<ChapterProgress> ChapterProgress => Set<ChapterProgress>();
    public DbSet<ReaderBookmark> ReaderBookmarks => Set<ReaderBookmark>();
    public DbSet<ReadingProfile> ReadingProfiles => Set<ReadingProfile>();
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>
    /// The per-user in-app notification inbox. Not to be confused with <see cref="Notifications"/>,
    /// which is the instance-wide list of outbound Discord/webhook connections.
    /// </summary>
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();

    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<SeriesTag> SeriesTags => Set<SeriesTag>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();
    public DbSet<SeriesRequest> SeriesRequests => Set<SeriesRequest>();
    public DbSet<UserAchievement> UserAchievements => Set<UserAchievement>();
    public DbSet<ReadingGoal> ReadingGoals => Set<ReadingGoal>();

    public override int SaveChanges()
    {
        StampOwner();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampOwner();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    /// <summary>
    /// Fills in <see cref="IUserOwned.UserId"/> on inserts that left it at 0, when a specific user is
    /// in scope. The backstop half of the ownership contract whose other half is the query filters —
    /// see <see cref="IUserOwned"/> for why an unowned row is the failure we want. Deliberately does
    /// not touch rows a job inserts (unrestricted scope, no user), nor existing rows: reassigning
    /// ownership on update would be a way to lose data, not to protect it.
    /// </summary>
    private void StampOwner()
    {
        if (_scope.Unrestricted || _scope.UserId == 0)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<IUserOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity.UserId == 0)
            {
                entry.Entity.UserId = _scope.UserId;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configures the Identity tables. Without this the AspNetUsers key, the normalized-name
        // unique indexes and the concurrency stamp are all missing, and sign-in fails at runtime
        // rather than at build time.
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MakiUser>(e =>
        {
            // Stored as the underlying int so the column is a plain integer a migration can seed
            // and a human can read, rather than EF's default enum-to-string for flags.
            e.Property(u => u.Permissions).HasConversion<int>();
        });

        modelBuilder.Entity<UserApiKey>(e =>
        {
            // The lookup index. Authentication hashes the presented key and matches this column,
            // so it must be unique and it must be indexed — every OPDS page image goes through it.
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.HasIndex(k => k.UserId);
            e.HasOne<MakiUser>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRootFolder>(e =>
        {
            e.HasKey(g => new { g.UserId, g.RootFolderId });
            e.HasOne<MakiUser>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RootFolder>().WithMany().HasForeignKey(g => g.RootFolderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserSetting>(e =>
        {
            e.HasKey(s => new { s.UserId, s.Key });
            e.HasOne<MakiUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => _scope.Unrestricted || s.UserId == _scope.UserId);
        });

        modelBuilder.Entity<UserSeriesState>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.SeriesId }).IsUnique();
            e.HasOne<MakiUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Series>().WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.Cascade);

            // SetNull, not Cascade: deleting a reading profile must un-pin the series that used it,
            // never delete the rating and per-series override that share the row.
            e.HasOne<ReadingProfile>().WithMany()
                .HasForeignKey(s => s.ReadingProfileId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(s => _scope.Unrestricted || s.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ReadingProfile>(e =>
        {
            // NOCASE for the same reason Tag.Label is: the name is free text and the picker shows
            // it, so "Webtoon" and "webtoon" existing side by side is a bug, not a feature.
            e.Property(p => p.Name).UseCollation("NOCASE");
            e.HasIndex(p => new { p.UserId, p.Name }).IsUnique();
            e.HasOne<MakiUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(p => _scope.Unrestricted || p.UserId == _scope.UserId);
        });

        modelBuilder.Entity<UserAchievement>(e =>
        {
            // One row per tier, so the unique key carries it. This is also the idempotency guard the
            // evaluator leans on: it runs on every chapter completion and on every page load, and a
            // lost race between the two must be a rejected insert rather than a duplicate badge.
            e.HasIndex(a => new { a.UserId, a.Key, a.Tier }).IsUnique();
            e.HasIndex(a => new { a.UserId, a.UnlockedAt });
            e.HasOne<MakiUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(a => _scope.Unrestricted || a.UserId == _scope.UserId);
        });

        modelBuilder.Entity<UserNotification>(e =>
        {
            // The feed's only ordering, and what the retention sweep scans.
            e.HasIndex(n => new { n.UserId, n.CreatedAt });

            // The badge count runs on every page load, so it gets its own index rather than filtering
            // the one above.
            e.HasIndex(n => new { n.UserId, n.ReadAt });

            e.HasOne<MakiUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(n => _scope.Unrestricted || n.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ReadingGoal>(e =>
        {
            // At most one goal per period and metric. "5 chapters a day" and "10 chapters a day" are
            // not two goals, they are one goal edited, and letting both exist makes "am I on track"
            // unanswerable.
            e.HasIndex(g => new { g.UserId, g.Period, g.Metric }).IsUnique();
            e.HasOne<MakiUser>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(g => _scope.Unrestricted || g.UserId == _scope.UserId);
        });

        modelBuilder.Entity<AuthEvent>(e =>
        {
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => a.UserId);
            // No FK to MakiUser: a failed login for a username that does not exist has no user to
            // point at, and the row must outlive a deleted account (UserName is denormalized for
            // exactly that).
        });

        modelBuilder.Entity<Series>(e =>
        {
            e.HasIndex(s => s.SortTitle);
            e.HasIndex(s => s.MangaBakaId);
            e.Property(s => s.Genres).HasConversion(StringListConverter.Instance, StringListComparer.Instance);
            e.Property(s => s.Tags).HasConversion(MetadataTagListConverter.Instance, MetadataTagListComparer.Instance);
            e.Property(s => s.AltTitles).HasConversion(StringListConverter.Instance, StringListComparer.Instance);
            e.HasMany(s => s.Chapters).WithOne(c => c.Series!).HasForeignKey(c => c.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.SourceMappings).WithOne(m => m.Series!).HasForeignKey(m => m.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.RootFolder).WithMany().HasForeignKey(s => s.RootFolderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.UserTags).WithMany(t => t.Series).UsingEntity<SeriesTag>(
                r => r.HasOne<Tag>().WithMany().HasForeignKey(j => j.TagId),
                l => l.HasOne<Series>().WithMany().HasForeignKey(j => j.SeriesId),
                j => j.ToTable("SeriesTags"));

            // Library access, enforced once here instead of at each of the dozens of places that
            // query series. A correlated EXISTS rather than an `IN` over a captured id set: the
            // grants are read fresh on every query (a revoked folder applies immediately) and the
            // SQL carries only scalar parameters, which keeps one plan in SQLite's cache instead of
            // one per distinct grant list. The two bypass flags are evaluated left-to-right, so an
            // admin's query never runs the subquery at all.
            e.HasQueryFilter(s =>
                _scope.Unrestricted ||
                _scope.AllRootFolders ||
                UserRootFolders.Any(g => g.UserId == _scope.UserId && g.RootFolderId == s.RootFolderId));
        });

        modelBuilder.Entity<Tag>(e =>
        {
            // NOCASE so "Action" and "action" can't both exist — tag input is free text and the
            // unique index is what stops the library growing two spellings of the same label.
            e.Property(t => t.Label).UseCollation("NOCASE");
            e.HasIndex(t => t.Label).IsUnique();
        });

        modelBuilder.Entity<SavedFilter>(e =>
        {
            e.HasIndex(f => new { f.UserId, f.SortOrder });
            e.HasOne<MakiUser>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(f => _scope.Unrestricted || f.UserId == _scope.UserId);
        });

        modelBuilder.Entity<SeriesRequest>(e =>
        {
            // The Requests page reads pending-first, newest-first, and the requester's own list reads
            // by owner — one index each rather than one composite, since the admin view deliberately
            // ignores the owner.
            e.HasIndex(r => new { r.Status, r.Created });
            e.HasIndex(r => new { r.UserId, r.Created });

            // REAL, for the same reason Chapter.Number is: a decimal lands in SQLite as TEXT, and
            // these two are compared against chapter numbers. Keeping both sides in one
            // representation is what stops "chapter 10" sorting between 1 and 2.
            e.Property(r => r.ChapterStart).HasConversion<double?>();
            e.Property(r => r.ChapterEnd).HasConversion<double?>();
            e.Property(r => r.OriginalChapterStart).HasConversion<double?>();
            e.Property(r => r.OriginalChapterEnd).HasConversion<double?>();

            e.HasOne<MakiUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);

            // Deleting the admin who resolved a request must not take the request with them, and
            // deleting the series must not either — see the entity's remarks.
            e.HasOne<MakiUser>().WithMany().HasForeignKey(r => r.ResolvedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<MakiUser>().WithMany().HasForeignKey(r => r.EditedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Series>().WithMany().HasForeignKey(r => r.SeriesId).OnDelete(DeleteBehavior.SetNull);

            e.HasQueryFilter(r => _scope.Unrestricted || r.UserId == _scope.UserId);
        });


    /// <summary>
    /// "The series this row hangs off is visible to the caller." Written as an EXISTS over
    /// <see cref="Series"/> rather than by repeating the root-folder join, so it inherits the series
    /// filter above and the two can never drift apart.
    /// <para>
    /// Every entity on the required end of a relationship to <c>Series</c> needs this, and not for
    /// tidiness: without it EF warns at model build that the required navigation may be filtered out,
    /// and — far worse — the child table is left <em>unfiltered</em>. A chapter, its file, its source
    /// mappings and its queue rows would all be readable by id for a series the caller was never
    /// granted, which is the whole access model bypassed one join short of the door.
    /// </para>
    /// </summary>
        modelBuilder.Entity<Chapter>(e =>
        {
            e.HasQueryFilter(c => _scope.Unrestricted || Series.Any(s => s.Id == c.SeriesId));

            // SQLite can't ORDER BY decimal (stored as TEXT); store as REAL instead.
            // Chapter numbers have at most 3 decimal places, well within double precision.
            e.Property(c => c.Number).HasConversion<double?>();
            e.HasIndex(c => new { c.SeriesId, c.Number, c.Volume, c.Language });
            e.HasOne(c => c.ChapterFile).WithMany().HasForeignKey(c => c.ChapterFileId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChapterFile>(e =>
        {
            e.HasQueryFilter(f => _scope.Unrestricted || Series.Any(s => s.Id == f.SeriesId));

            e.HasIndex(f => f.SeriesId);

            // Home's "recently added" rail is an ORDER BY DateAdded DESC LIMIT n; without this it
            // is a full scan plus a sort of every file in the library on every landing-page load.
            e.HasIndex(f => f.DateAdded);

            e.HasOne<Series>().WithMany().HasForeignKey(f => f.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceMapping>(e =>
        {
            e.HasQueryFilter(m => _scope.Unrestricted || Series.Any(s => s.Id == m.SeriesId));

            e.HasIndex(m => new { m.SeriesId, m.SourceName }).IsUnique();
        });

        modelBuilder.Entity<ChapterSourceLink>(e =>
        {
            e.HasKey(l => new { l.ChapterId, l.SourceMappingId });
            e.HasIndex(l => l.SourceMappingId);
            e.HasOne(l => l.Chapter).WithMany(c => c.SourceLinks)
                .HasForeignKey(l => l.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.SourceMapping).WithMany(m => m.ChapterLinks)
                .HasForeignKey(l => l.SourceMappingId).OnDelete(DeleteBehavior.Cascade);

            // A link is visible exactly when its chapter's series is visible. Spell the series
            // EXISTS out here so adding a join table cannot bypass root-folder grants.
            e.HasQueryFilter(l =>
                _scope.Unrestricted ||
                Series.Any(s => s.Chapters.Any(c => c.Id == l.ChapterId)));
        });

        modelBuilder.Entity<DownloadQueueItem>(e =>
        {
            e.HasQueryFilter(q => _scope.Unrestricted || Series.Any(s => s.Id == q.SeriesId));

            e.HasIndex(q => q.Status);
            e.HasOne(q => q.Series).WithMany().HasForeignKey(q => q.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(q => q.Chapter).WithMany().HasForeignKey(q => q.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(q => q.SourceMapping).WithMany().HasForeignKey(q => q.SourceMappingId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AppConfigEntry>(e =>
        {
            e.HasKey(c => c.Key);
        });

        modelBuilder.Entity<ScrobbleToken>(e =>
        {
            // One remote account per user per service. The client id/secret the token was minted
            // against stays in AppConfig — an app registration is per-instance, its tokens are not.
            e.HasKey(t => new { t.UserId, t.Service });
            e.HasOne<MakiUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(t => _scope.Unrestricted || t.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ScrobbleMapping>(e =>
        {
            e.HasIndex(m => new { m.UserId, m.KavitaSeriesId, m.Service }).IsUnique();
            e.HasOne<MakiUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(m => _scope.Unrestricted || m.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ScrobbleSyncState>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.KavitaSeriesId, s.Service }).IsUnique();
            e.HasIndex(s => s.SyncedAt);
            e.HasOne<MakiUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => _scope.Unrestricted || s.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ScrobbleUnmatched>(e =>
        {
            e.HasIndex(u => new { u.UserId, u.KavitaSeriesId, u.Service }).IsUnique();
            e.HasOne<MakiUser>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(u => _scope.Unrestricted || u.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ScrobbleLogEntry>(e =>
        {
            e.HasIndex(l => new { l.UserId, l.Timestamp });
            e.HasOne<MakiUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(l => _scope.Unrestricted || l.UserId == _scope.UserId);
        });

        modelBuilder.Entity<StatsEvent>(e =>
        {
            e.HasIndex(s => new { s.Type, s.Timestamp });
            e.HasIndex(s => s.SeriesId);
            e.HasIndex(s => new { s.UserId, s.Timestamp });

            // Adoption looks orphans up by key on every series add, and the aggregation groups by
            // it, so it is read far more than the severable FK above.
            e.HasIndex(s => s.SeriesKey);

            e.HasOne(s => s.Series).WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.SetNull);

            // Cascade, not SetNull: a deleted account's reads are personal history, and nulling them
            // would silently promote them to library-wide events every remaining user can see.
            e.HasOne<MakiUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);

            // The one user-owned table whose filter is not plain equality. A null UserId is a
            // *library* event — a series added, a chapter downloaded — with no reader behind it, so
            // it stays visible to everyone. See StatsEvent.UserId for why forcing it non-null would
            // hand the whole back catalogue to whoever happened to be user 1.
            e.HasQueryFilter(s => _scope.Unrestricted || s.UserId == null || s.UserId == _scope.UserId);
        });

        modelBuilder.Entity<SeriesScrobbleState>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.SeriesId, s.Service }).IsUnique();
            e.HasOne<Series>().WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MakiUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => _scope.Unrestricted || s.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ReadingState>(e =>
        {
            // No HasFilter here: SQLite treats NULLs as distinct in a unique index, so this
            // already permits any number of native (Kavita-less) rows.
            e.HasIndex(r => new { r.UserId, r.KavitaSeriesId }).IsUnique();

            // Two indexes over the same column, so both MUST use the named HasIndex overload:
            // the unnamed one keys indexes by property set, meaning a second call reconfigures
            // the first instead of adding one (and the migration then drops the plain index
            // without recreating it).

            // SQLite does not auto-index FK columns, and every reader write plus the read-count
            // projections in SeriesController look rows up by SeriesId. The partial index below
            // cannot serve those — SQLite only uses a partial index when the query's WHERE
            // provably implies the index's.
            e.HasIndex(r => new { r.UserId, r.SeriesId }, "IX_ReadingStates_UserSeries");

            // "At most one native row per series." Deliberately NOT a plain unique index on
            // SeriesId: two Kavita series can resolve to one local series (the library index is
            // built from both title and folder name), so duplicates are legal and every reader
            // orders by MaxChapter to pick one — see ReadingProgressService.PickAsync for why
            // that key and not UpdatedAt. A plain unique index would fail Migrate() at startup
            // on real databases.
            e.HasIndex(r => new { r.UserId, r.SeriesId }, "IX_ReadingStates_NativeSeries").IsUnique()
                .HasFilter("\"SeriesId\" IS NOT NULL AND \"KavitaSeriesId\" IS NULL");

            // Tombstones — both keys null, the shape a hard delete leaves behind.
            // SeriesIdentityService looks these up on *every* series create, and a folder import
            // creates thousands in a row; without this the probe is a full table scan each time,
            // because neither index above can serve "SeriesId IS NULL". The filter is written to
            // match that predicate exactly: SQLite only uses a partial index when the query's WHERE
            // provably implies the index's, so any drift here silently returns it to a scan.
            //
            // KavitaSeriesId leads, and the order is load-bearing rather than a preference: every
            // indexed row holds NULL in both columns, so neither orders anything, but EF suppresses
            // its own convention index for a foreign key as soon as some other index *starts* with
            // that column. Leading with SeriesId therefore silently deletes IX_ReadingStates_SeriesId
            // and hands its job to an index that is filtered and so cannot serve a plain
            // "SeriesId = ?" lookup at all — the per-series reader writes described above.
            e.HasIndex(r => new { r.KavitaSeriesId, r.SeriesId }, "IX_ReadingStates_Tombstones")
                .HasFilter("\"SeriesId\" IS NULL AND \"KavitaSeriesId\" IS NULL");

            e.HasOne<Series>().WithMany().HasForeignKey(r => r.SeriesId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<MakiUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(r => _scope.Unrestricted || r.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ChapterProgress>(e =>
        {
            e.HasIndex(p => new { p.UserId, p.ChapterId }).IsUnique();
            e.HasIndex(p => new { p.UserId, p.SeriesId });

            // Both Home reading rails and OPDS's on-deck feed walk this newest-first for one user;
            // without UserId leading, a second reader's rows dilute every bounded scan.
            e.HasIndex(p => new { p.UserId, p.UpdatedAt });

            e.HasOne<Series>().WithMany().HasForeignKey(p => p.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Chapter>().WithMany().HasForeignKey(p => p.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MakiUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(p => _scope.Unrestricted || p.UserId == _scope.UserId);
        });

        modelBuilder.Entity<ReaderBookmark>(e =>
        {
            e.HasIndex(b => new { b.UserId, b.ChapterId, b.PageIndex }).IsUnique();
            e.HasIndex(b => new { b.UserId, b.SeriesId });
            e.HasOne<Series>().WithMany().HasForeignKey(b => b.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Chapter>().WithMany().HasForeignKey(b => b.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MakiUser>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(b => _scope.Unrestricted || b.UserId == _scope.UserId);
        });
    }
}
