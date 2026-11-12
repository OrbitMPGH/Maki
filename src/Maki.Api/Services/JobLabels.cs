using Maki.Api.Jobs;
using Maki.Api.Localization;

namespace Maki.Api.Services;

/// <summary>
/// The catalogue key naming each scheduled job, by job class. Job health rows, history, the inbox and
/// outbound notifications store this key as the <c>job</c> value and word it at read time; a job
/// missing here keeps the scheduler's own key, which renders as itself.
/// </summary>
public static class JobLabels
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        [nameof(AnimeSignalJob)] = "health.job.animeSignals",
        [nameof(ArtifactIdleUnloadJob)] = "health.job.artifactIdleUnload",
        [nameof(BrowserIdleShutdownJob)] = "health.job.browserIdleShutdown",
        [nameof(ChapterFileMeasureJob)] = "health.job.chapterFileMeasure",
        [nameof(CheckForUpdatesJob)] = "health.job.checkForUpdates",
        [nameof(CoReadJob)] = "health.job.coRead",
        [nameof(CompletedDownloadJob)] = "health.job.completedDownloads",
        [nameof(CustomSourceHealthJob)] = "health.job.customSourceHealth",
        [nameof(CustomSourceRepositoryJob)] = "health.job.customSourceRepositories",
        [nameof(DiscoverCacheWarmJob)] = "health.job.discoverCacheWarm",
        [nameof(EmbedderIdleUnloadJob)] = "health.job.embedderIdleUnload",
        [nameof(EmbeddingIndexJob)] = "health.job.embeddingIndex",
        [nameof(FollowedCreatorReleaseJob)] = "health.job.followedCreatorReleases",
        [nameof(HealthCheckJob)] = "health.job.healthCheck",
        [nameof(HousekeepingJob)] = "health.job.housekeeping",
        [nameof(ImageCacheRebuildJob)] = "health.job.imageCacheRebuild",
        [nameof(ImportListJob)] = "health.job.importLists",
        [nameof(KoreaderDocumentIndexJob)] = "health.job.koreaderDocumentIndex",
        [nameof(MangaBakaDumpRefreshJob)] = "health.job.mangaBakaDumpRefresh",
        [nameof(MangaBakaTableBackfillJob)] = "health.job.mangaBakaTableBackfill",
        [nameof(MetadataRefreshJob)] = "health.job.metadataRefresh",
        [nameof(PrebuiltIndexJob)] = "health.job.prebuiltIndex",
        [nameof(ReadFileCleanupJob)] = "health.job.readFileCleanup",
        [nameof(ReaderCohortJob)] = "health.job.readerCohorts",
        [nameof(ReadingStatusAutoJob)] = "health.job.readingStatusRules",
        [nameof(RecoGraphJob)] = "health.job.recoGraph",
        [nameof(RefreshMonitoredSeriesJob)] = "health.job.refreshMonitored",
        [nameof(RetryFailedDownloadsJob)] = "health.job.retryFailedDownloads",
        [nameof(ScheduledBackupJob)] = "health.job.scheduledBackup",
        [nameof(ScrobbleJob)] = "health.job.scrobble",
        [nameof(SmartDownloadJob)] = "health.job.smartDownload",
        [nameof(TasteVectorJob)] = "health.job.tasteVectors",
        [nameof(TorrentRemovalJob)] = "health.job.torrentRemoval",
        [nameof(UpgradeScanJob)] = "health.job.upgradeScan",
        [nameof(UpgradeVolumeSearchJob)] = "health.job.upgradeVolumeSearch",
        [nameof(UpscalerIdleUnloadJob)] = "health.job.upscalerIdleUnload",
    };

    private static readonly HashSet<string> Known = [.. Keys.Values];

    /// <summary>The value to store as <c>job</c>: the label key for a known job class, else the scheduler's key.</summary>
    public static string For(Type? jobType, string schedulerKey) =>
        jobType is not null && Keys.TryGetValue(jobType.Name, out var key) ? key : schedulerKey;

    /// <summary>Whether a stored <c>job</c> value is a label key rather than a scheduler key from an older row.</summary>
    public static bool IsLabel(string? value) => value is not null && Known.Contains(value);

    /// <summary>Words a stored <c>job</c> value in the request's language, leaving an older row's scheduler key as it is.</summary>
    public static Dictionary<string, object?> Worded(Dictionary<string, object?> values, ILocalizer localizer)
    {
        if (values.GetValueOrDefault("job") is string job && IsLabel(job))
            values["job"] = localizer.Get(job);
        return values;
    }

    internal static IReadOnlyCollection<string> AllJobTypes => Keys.Keys;
}
