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
            e.Property(c => c.Number).HasPrecision(9, 3);
            e.HasIndex(c => new { c.SeriesId, c.Number, c.Volume, c.Language });
            e.HasOne(c => c.ChapterFile).WithMany().HasForeignKey(c => c.ChapterFileId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChapterFile>(e =>
        {
            e.HasIndex(f => f.SeriesId);
        });

        modelBuilder.Entity<SourceMapping>(e =>
        {
            e.HasIndex(m => new { m.SeriesId, m.SourceName }).IsUnique();
        });

        modelBuilder.Entity<DownloadQueueItem>(e =>
        {
            e.HasIndex(q => q.Status);
            e.HasOne(q => q.Chapter).WithMany().HasForeignKey(q => q.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(q => q.SourceMapping).WithMany().HasForeignKey(q => q.SourceMappingId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AppConfigEntry>(e =>
        {
            e.HasKey(c => c.Key);
        });
    }
}
