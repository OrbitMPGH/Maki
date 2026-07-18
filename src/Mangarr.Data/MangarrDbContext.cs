using Mangarr.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Data;

public class MangarrDbContext(DbContextOptions<MangarrDbContext> options) : DbContext(options)
{
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
    public DbSet<ScrobbleUnmatched> ScrobbleUnmatched => Set<ScrobbleUnmatched>();
    public DbSet<ScrobbleLogEntry> ScrobbleLog => Set<ScrobbleLogEntry>();
    public DbSet<StatsEvent> StatsEvents => Set<StatsEvent>();
    public DbSet<ReadingState> ReadingStates => Set<ReadingState>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Series>(e =>
        {
            e.HasIndex(s => s.SortTitle);
            e.HasIndex(s => s.MangaBakaId);
            e.Property(s => s.Genres).HasConversion(StringListConverter.Instance, StringListComparer.Instance);
            e.Property(s => s.Tags).HasConversion(StringListConverter.Instance, StringListComparer.Instance);
            e.HasMany(s => s.Chapters).WithOne(c => c.Series!).HasForeignKey(c => c.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.SourceMappings).WithOne(m => m.Series!).HasForeignKey(m => m.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.RootFolder).WithMany().HasForeignKey(s => s.RootFolderId).OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<ReadingState>(e =>
        {
            e.HasIndex(r => r.KavitaSeriesId).IsUnique();
            e.HasOne<Series>().WithMany().HasForeignKey(r => r.SeriesId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
