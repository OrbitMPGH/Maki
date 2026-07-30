using Maki.Core.Entities;
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
public class MakiDbContext(DbContextOptions<MakiDbContext> options)
    : IdentityUserContext<MakiUser, int>(options)
{
    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<UserRootFolder> UserRootFolders => Set<UserRootFolder>();
    public DbSet<AuthEvent> AuthEvents => Set<AuthEvent>();

    public DbSet<Series> Series => Set<Series>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<ChapterFile> ChapterFiles => Set<ChapterFile>();
    public DbSet<SourceMapping> SourceMappings => Set<SourceMapping>();
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
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<SeriesTag> SeriesTags => Set<SeriesTag>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();

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
            e.Property(s => s.Tags).HasConversion(StringListConverter.Instance, StringListComparer.Instance);
            e.HasMany(s => s.Chapters).WithOne(c => c.Series!).HasForeignKey(c => c.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.SourceMappings).WithOne(m => m.Series!).HasForeignKey(m => m.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.RootFolder).WithMany().HasForeignKey(s => s.RootFolderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.UserTags).WithMany(t => t.Series).UsingEntity<SeriesTag>(
                r => r.HasOne<Tag>().WithMany().HasForeignKey(j => j.TagId),
                l => l.HasOne<Series>().WithMany().HasForeignKey(j => j.SeriesId),
                j => j.ToTable("SeriesTags"));
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
            e.HasIndex(f => f.SortOrder);
        });

        modelBuilder.Entity<Chapter>(e =>
        {
            // SQLite can't ORDER BY decimal (stored as TEXT); store as REAL instead.
            // Chapter numbers have at most 3 decimal places, well within double precision.
            e.Property(c => c.Number).HasConversion<double?>();
            e.HasIndex(c => new { c.SeriesId, c.Number, c.Volume, c.Language });
            e.HasOne(c => c.ChapterFile).WithMany().HasForeignKey(c => c.ChapterFileId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChapterFile>(e =>
        {
            e.HasIndex(f => f.SeriesId);

            // Home's "recently added" rail is an ORDER BY DateAdded DESC LIMIT n; without this it
            // is a full scan plus a sort of every file in the library on every landing-page load.
            e.HasIndex(f => f.DateAdded);

            e.HasOne<Series>().WithMany().HasForeignKey(f => f.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceMapping>(e =>
        {
            e.HasIndex(m => new { m.SeriesId, m.SourceName }).IsUnique();
        });

        modelBuilder.Entity<DownloadQueueItem>(e =>
        {
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
            e.HasKey(t => t.Service);
        });

        modelBuilder.Entity<ScrobbleMapping>(e =>
        {
            e.HasIndex(m => new { m.KavitaSeriesId, m.Service }).IsUnique();
        });

        modelBuilder.Entity<ScrobbleSyncState>(e =>
        {
            e.HasIndex(s => new { s.KavitaSeriesId, s.Service }).IsUnique();
            e.HasIndex(s => s.SyncedAt);
        });

        modelBuilder.Entity<ScrobbleUnmatched>(e =>
        {
            e.HasIndex(u => new { u.KavitaSeriesId, u.Service }).IsUnique();
        });

        modelBuilder.Entity<StatsEvent>(e =>
        {
            e.HasIndex(s => new { s.Type, s.Timestamp });
            e.HasIndex(s => s.SeriesId);
            e.HasOne(s => s.Series).WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SeriesScrobbleState>(e =>
        {
            e.HasIndex(s => new { s.SeriesId, s.Service }).IsUnique();
            e.HasOne<Series>().WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadingState>(e =>
        {
            // No HasFilter here: SQLite treats NULLs as distinct in a unique index, so this
            // already permits any number of native (Kavita-less) rows.
            e.HasIndex(r => r.KavitaSeriesId).IsUnique();

            // Two indexes over the same column, so both MUST use the named HasIndex overload:
            // the unnamed one keys indexes by property set, meaning a second call reconfigures
            // the first instead of adding one (and the migration then drops the plain index
            // without recreating it).

            // SQLite does not auto-index FK columns, and every reader write plus the read-count
            // projections in SeriesController look rows up by SeriesId. The partial index below
            // cannot serve those — SQLite only uses a partial index when the query's WHERE
            // provably implies the index's.
            e.HasIndex(r => r.SeriesId, "IX_ReadingStates_SeriesId");

            // "At most one native row per series." Deliberately NOT a plain unique index on
            // SeriesId: two Kavita series can resolve to one local series (the library index is
            // built from both title and folder name), so duplicates are legal and every reader
            // orders by MaxChapter to pick one — see ReadingProgressService.PickAsync for why
            // that key and not UpdatedAt. A plain unique index would fail Migrate() at startup
            // on real databases.
            e.HasIndex(r => r.SeriesId, "IX_ReadingStates_NativeSeries").IsUnique()
                .HasFilter("\"SeriesId\" IS NOT NULL AND \"KavitaSeriesId\" IS NULL");

            e.HasOne<Series>().WithMany().HasForeignKey(r => r.SeriesId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChapterProgress>(e =>
        {
            e.HasIndex(p => p.ChapterId).IsUnique();
            e.HasIndex(p => p.SeriesId);
            e.HasIndex(p => p.UpdatedAt);
            e.HasOne<Series>().WithMany().HasForeignKey(p => p.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Chapter>().WithMany().HasForeignKey(p => p.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReaderBookmark>(e =>
        {
            e.HasIndex(b => new { b.ChapterId, b.PageIndex }).IsUnique();
            e.HasIndex(b => b.SeriesId);
            e.HasOne<Series>().WithMany().HasForeignKey(b => b.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Chapter>().WithMany().HasForeignKey(b => b.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
