using System.Globalization;
using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
public class SettingsController(
    SettingsService settings,
    FlareSolverrClient flareSolverr,
    Maki.Core.Indexers.ProwlarrClient prowlarr,
    Maki.Core.Download.QBittorrentClient qbittorrent,
    Maki.Core.Kavita.KavitaClient kavita,
    ConfigFileProvider configFile,
    SourceRegistry sourceRegistry,
    MangaBakaDumpService mangaBakaDump,
    EmbeddingModelStore embeddingModel,
    EmbeddingStore embeddingStore,
    EmbeddingIndexStatus embeddingStatus,
    SeriesEmbeddingIndexer embeddingIndexer,
    EmbeddingOptions embeddingOptions,
    PrebuiltIndexInstaller prebuiltIndex,
    EmbeddingModelSwitcher modelSwitcher,
    Maki.Data.MakiDbContext db,
    UpdateCheckService updateCheck,
    ISchedulerFactory schedulerFactory) : ControllerBase
{
    public record FlareSolverrSettings(string? Url);
    public record ProwlarrSettings(string? Url, string? ApiKey);
    public record QBittorrentSettings(
        string? Url, string? Username, string? Password, string? Category, string? PathMapFrom, string? PathMapTo);
    public record MetadataSettings(bool UseLocalDb);
    public record MetadataSettingsResponse(bool UseLocalDb, bool DumpPresent, long? DumpSizeBytes, DateTime? DumpRefreshedAt);
    public record MonitoringSettings(bool UnmonitorSpecials);
    public record LibrarySettings(bool WriteComicInfo, string FolderNamingMode);
    public record SetupStatus(bool Completed);
    public record DownloadSettings(int ConcurrentChapters, bool RetryEnabled, int RetryMaxAttempts);
    public record BackupSettings(int Retention);
    public record UpdateSettings(bool CheckForUpdates);
    public record DiscoverSettings(string MaxContentRating);
    public record KavitaSettings(string? Url, string? ApiKey, string? PathMapFrom, string? PathMapTo);

    /// <summary>
    /// Blank clears the setting; anything else must be an absolute http(s) URL. Rejecting garbage
    /// on save means the error names the field the user just typed in, instead of surfacing later
    /// as a confusing connection failure when they click Test.
    /// </summary>
    private static bool IsValidServiceUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private static string UrlError(string service) =>
        $"{service} URL must be a full http:// or https:// address (e.g. http://localhost:8080), or blank to clear it";

    [HttpGet("monitoring")]
    public async Task<IActionResult> GetMonitoring(CancellationToken ct) => Ok(new MonitoringSettings(
        await settings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"));

    [HttpPut("monitoring")]
    public async Task<IActionResult> SetMonitoring([FromBody] MonitoringSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.MonitoringUnmonitorSpecials, request.UnmonitorSpecials ? "true" : "false", ct);
        return Ok(request);
    }

    [HttpGet("library")]
    public async Task<IActionResult> GetLibrary(CancellationToken ct)
    {
        var mode = await settings.GetAsync(SettingKeys.LibraryFolderNamingMode, ct);
        return Ok(new LibrarySettings(
            await settings.GetAsync(SettingKeys.LibraryWriteComicInfo, ct) != "false",
            Maki.Core.Naming.FolderNamingMode.IsValid(mode) ? mode! : Maki.Core.Naming.FolderNamingMode.Default));
    }

    [HttpPut("library")]
    public async Task<IActionResult> SetLibrary([FromBody] LibrarySettings request, CancellationToken ct)
    {
        if (!Maki.Core.Naming.FolderNamingMode.IsValid(request.FolderNamingMode))
        {
            return BadRequest(new { error = $"Unknown folder naming mode: {request.FolderNamingMode}" });
        }

        await settings.SetAsync(SettingKeys.LibraryWriteComicInfo, request.WriteComicInfo ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.LibraryFolderNamingMode, request.FolderNamingMode, ct);
        return Ok(request);
    }

    /// <summary>
    /// The first-run guide shows only when this reports not-completed. The flag is tri-state:
    /// "true"/"false" are explicit (finishing/skipping vs. the "Run setup guide" button re-opening
    /// it), and unset falls back to "has a root folder" — an existing user upgrading into this
    /// feature already has one and shouldn't be nagged, a fresh install doesn't and gets the guide.
    /// </summary>
    [HttpGet("setup")]
    public async Task<IActionResult> GetSetup(CancellationToken ct)
    {
        var stored = await settings.GetAsync(SettingKeys.SetupCompleted, ct);
        if (stored is not null)
        {
            return Ok(new SetupStatus(stored == "true"));
        }

        var hasRootFolder = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .AnyAsync(db.RootFolders, ct);
        return Ok(new SetupStatus(hasRootFolder));
    }

    [HttpPut("setup")]
    public async Task<IActionResult> SetSetup([FromBody] SetupStatus request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.SetupCompleted, request.Completed ? "true" : "false", ct);
        return Ok(request);
    }

    [HttpGet("discover")]
    public async Task<IActionResult> GetDiscover(CancellationToken ct) =>
        Ok(new DiscoverSettings(await ContentRating.GetMaxAsync(settings, ct)));

    [HttpPut("discover")]
    public async Task<IActionResult> SetDiscover([FromBody] DiscoverSettings request, CancellationToken ct)
    {
        if (!ContentRating.IsValid(request.MaxContentRating))
        {
            return BadRequest(new { error = $"Unknown content rating: {request.MaxContentRating}" });
        }

        await settings.SetAsync(SettingKeys.DiscoverMaxContentRating, request.MaxContentRating, ct);
        return await GetDiscover(ct);
    }

    [HttpGet("download")]
    public async Task<IActionResult> GetDownload(CancellationToken ct) => Ok(new DownloadSettings(
        int.TryParse(await settings.GetAsync(SettingKeys.DownloadConcurrentChapters, ct), out var n) ? n : 2,
        await settings.GetAsync(SettingKeys.DownloadRetryEnabled, ct) != "false",
        int.TryParse(await settings.GetAsync(SettingKeys.DownloadRetryMaxAttempts, ct), out var r) ? r : 5));

    [HttpPut("download")]
    public async Task<IActionResult> SetDownload([FromBody] DownloadSettings request, CancellationToken ct)
    {
        if (request.ConcurrentChapters is < 1 or > 8)
        {
            return BadRequest(new { error = "Concurrent chapter downloads must be between 1 and 8" });
        }

        if (request.RetryMaxAttempts is < 1 or > 20)
        {
            return BadRequest(new { error = "Retry attempts must be between 1 and 20" });
        }

        await settings.SetAsync(
            SettingKeys.DownloadConcurrentChapters,
            request.ConcurrentChapters.ToString(CultureInfo.InvariantCulture),
            ct);
        await settings.SetAsync(SettingKeys.DownloadRetryEnabled, request.RetryEnabled ? "true" : "false", ct);
        await settings.SetAsync(
            SettingKeys.DownloadRetryMaxAttempts,
            request.RetryMaxAttempts.ToString(CultureInfo.InvariantCulture),
            ct);
        return Ok(request);
    }

    [HttpGet("backup")]
    public async Task<IActionResult> GetBackup(CancellationToken ct) => Ok(new BackupSettings(
        int.TryParse(await settings.GetAsync(SettingKeys.BackupRetention, ct), out var n) ? n : 5));

    [HttpPut("backup")]
    public async Task<IActionResult> SetBackup([FromBody] BackupSettings request, CancellationToken ct)
    {
        if (request.Retention is < 1 or > 50)
        {
            return BadRequest(new { error = "Backups to keep must be between 1 and 50" });
        }

        await settings.SetAsync(
            SettingKeys.BackupRetention,
            request.Retention.ToString(CultureInfo.InvariantCulture),
            ct);
        return Ok(request);
    }

    public record SourcePrioritySettings(List<string> Order);

    /// <summary>
    /// Full list of registered source names, ordered by preference: sources named in the stored
    /// priority setting come first (in that order), then any remaining registered sources.
    /// </summary>
    [HttpGet("sources/priority")]
    public async Task<IActionResult> GetSourcePriority(CancellationToken ct)
    {
        var ordered = SourceMatchService.OrderSources(
            sourceRegistry.All, await settings.GetAsync(SettingKeys.SourcePriorityOrder, ct));
        return Ok(new SourcePrioritySettings(ordered.Select(s => s.Name).ToList()));
    }

    [HttpPut("sources/priority")]
    public async Task<IActionResult> SetSourcePriority([FromBody] SourcePrioritySettings request, CancellationToken ct)
    {
        var unknown = request.Order.Where(name => sourceRegistry.Find(name) is null).ToList();
        if (unknown.Count > 0)
        {
            return BadRequest(new { error = $"Unknown source(s): {string.Join(", ", unknown)}" });
        }

        await settings.SetAsync(SettingKeys.SourcePriorityOrder, string.Join(',', request.Order), ct);
        return await GetSourcePriority(ct);
    }

    [HttpGet("prowlarr")]
    public async Task<IActionResult> GetProwlarr(CancellationToken ct) => Ok(new ProwlarrSettings(
        await settings.GetAsync(SettingKeys.ProwlarrUrl, ct),
        await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct)));

    [HttpPut("prowlarr")]
    public async Task<IActionResult> SetProwlarr([FromBody] ProwlarrSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("Prowlarr") });
        }

        await settings.SetAsync(SettingKeys.ProwlarrUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.ProwlarrApiKey, request.ApiKey, ct);
        return Ok(request);
    }

    public record ProwlarrOptions(string? IndexerIds, string? Categories);

    [HttpGet("prowlarr/options")]
    public async Task<IActionResult> GetProwlarrOptions(CancellationToken ct) => Ok(new ProwlarrOptions(
        await settings.GetAsync(SettingKeys.ProwlarrIndexerIds, ct),
        await settings.GetAsync(SettingKeys.ProwlarrCategories, ct)));

    [HttpPut("prowlarr/options")]
    public async Task<IActionResult> SetProwlarrOptions([FromBody] ProwlarrOptions request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.ProwlarrIndexerIds, request.IndexerIds, ct);
        await settings.SetAsync(SettingKeys.ProwlarrCategories, request.Categories, ct);
        return Ok(request);
    }

    /// <summary>Proxies Prowlarr's indexer list (with category capabilities) for the settings UI.</summary>
    [HttpGet("prowlarr/indexers")]
    public async Task<IActionResult> GetProwlarrIndexers(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.ProwlarrUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { error = "Prowlarr is not configured" });
        }

        var indexers = await prowlarr.GetIndexersAsync(url, apiKey, ct);
        return Ok(indexers.Select(i => new
        {
            i.Id,
            i.Name,
            i.Enable,
            i.Protocol,
            Categories = Flatten(i.Capabilities?.Categories)
                .Where(c => c.Name is not null)
                .Select(c => new { c.Id, c.Name })
                .DistinctBy(c => c.Id)
                .OrderBy(c => c.Id)
        }));

        static IEnumerable<Maki.Core.Indexers.ProwlarrClient.ProwlarrCategory> Flatten(
            IEnumerable<Maki.Core.Indexers.ProwlarrClient.ProwlarrCategory>? categories) =>
            categories?.SelectMany(c => new[] { c }.Concat(Flatten(c.SubCategories))) ?? [];
    }

    [HttpPost("prowlarr/test")]
    public async Task<IActionResult> TestProwlarr([FromBody] ProwlarrSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.ProwlarrUrl, ct);
        var apiKey = request.ApiKey ?? await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { error = "URL and API key are required" });
        }

        return await prowlarr.PingAsync(url, apiKey, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "Prowlarr did not respond (check URL/API key)" });
    }

    [HttpGet("qbittorrent")]
    public async Task<IActionResult> GetQBittorrent(CancellationToken ct) => Ok(new QBittorrentSettings(
        await settings.GetAsync(SettingKeys.QBittorrentUrl, ct),
        await settings.GetAsync(SettingKeys.QBittorrentUsername, ct),
        await settings.GetAsync(SettingKeys.QBittorrentPassword, ct),
        await settings.GetAsync(SettingKeys.QBittorrentCategory, ct) ?? "maki",
        await settings.GetAsync(SettingKeys.QBittorrentPathMapFrom, ct),
        await settings.GetAsync(SettingKeys.QBittorrentPathMapTo, ct)));

    [HttpPut("qbittorrent")]
    public async Task<IActionResult> SetQBittorrent([FromBody] QBittorrentSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("qBittorrent") });
        }

        await settings.SetAsync(SettingKeys.QBittorrentUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.QBittorrentUsername, request.Username, ct);
        await settings.SetAsync(SettingKeys.QBittorrentPassword, request.Password, ct);
        await settings.SetAsync(SettingKeys.QBittorrentCategory, request.Category, ct);
        await settings.SetAsync(SettingKeys.QBittorrentPathMapFrom, request.PathMapFrom, ct);
        await settings.SetAsync(SettingKeys.QBittorrentPathMapTo, request.PathMapTo, ct);
        return Ok(request);
    }

    [HttpPost("qbittorrent/test")]
    public async Task<IActionResult> TestQBittorrent([FromBody] QBittorrentSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.QBittorrentUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "URL is required" });
        }

        var username = request.Username ?? await settings.GetAsync(SettingKeys.QBittorrentUsername, ct) ?? string.Empty;
        var password = request.Password ?? await settings.GetAsync(SettingKeys.QBittorrentPassword, ct) ?? string.Empty;

        return await qbittorrent.PingAsync(url, username, password, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "qBittorrent login failed" });
    }

    [HttpGet("kavita")]
    public async Task<IActionResult> GetKavita(CancellationToken ct) => Ok(new KavitaSettings(
        await settings.GetAsync(SettingKeys.KavitaUrl, ct),
        await settings.GetAsync(SettingKeys.KavitaApiKey, ct),
        await settings.GetAsync(SettingKeys.KavitaPathMapFrom, ct),
        await settings.GetAsync(SettingKeys.KavitaPathMapTo, ct)));

    [HttpPut("kavita")]
    public async Task<IActionResult> SetKavita([FromBody] KavitaSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("Kavita") });
        }

        await settings.SetAsync(SettingKeys.KavitaUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.KavitaApiKey, request.ApiKey, ct);
        await settings.SetAsync(SettingKeys.KavitaPathMapFrom, request.PathMapFrom, ct);
        await settings.SetAsync(SettingKeys.KavitaPathMapTo, request.PathMapTo, ct);
        return Ok(request);
    }

    [HttpPost("kavita/test")]
    public async Task<IActionResult> TestKavita([FromBody] KavitaSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var apiKey = request.ApiKey ?? await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { error = "URL and API key are required" });
        }

        return await kavita.PingAsync(url, apiKey, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "Kavita did not respond (check URL/API key)" });
    }

    [HttpGet("flaresolverr")]
    public async Task<IActionResult> GetFlareSolverr(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        return Ok(new FlareSolverrSettings(url));
    }

    [HttpPut("flaresolverr")]
    public async Task<IActionResult> SetFlareSolverr([FromBody] FlareSolverrSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("FlareSolverr") });
        }

        await settings.SetAsync(SettingKeys.FlareSolverrUrl, request.Url, ct);
        return Ok(new FlareSolverrSettings(request.Url));
    }

    [HttpPost("flaresolverr/test")]
    public async Task<IActionResult> TestFlareSolverr([FromBody] FlareSolverrSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "No FlareSolverr URL configured" });
        }

        var ok = await flareSolverr.PingAsync(url, ct);
        return ok
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "FlareSolverr did not respond" });
    }

    [HttpGet("metadata")]
    public async Task<IActionResult> GetMetadata(CancellationToken ct)
    {
        var useLocalDb = await settings.GetAsync(SettingKeys.MangaBakaUseLocalDb, ct) != "false";
        var status = await mangaBakaDump.GetStatusAsync(ct);
        return Ok(new MetadataSettingsResponse(useLocalDb, status.Present, status.SizeBytes, status.RefreshedAt));
    }

    [HttpPut("metadata")]
    public async Task<IActionResult> SetMetadata([FromBody] MetadataSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.MangaBakaUseLocalDb, request.UseLocalDb ? "true" : "false", ct);
        return await GetMetadata(ct);
    }

    [HttpPost("metadata/refresh")]
    public async Task<IActionResult> RefreshMetadataDump(CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.TriggerJob(MangaBakaDumpRefreshJob.Key, ct);
        return Ok(new { started = true });
    }

    [HttpGet("updates")]
    public async Task<IActionResult> GetUpdates(CancellationToken ct) => Ok(new UpdateSettings(
        await settings.GetAsync(SettingKeys.UpdatesCheckForUpdates, ct) != "false"));

    [HttpPut("updates")]
    public async Task<IActionResult> SetUpdates([FromBody] UpdateSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.UpdatesCheckForUpdates, request.CheckForUpdates ? "true" : "false", ct);
        return Ok(request);
    }

    [HttpPost("updates/check")]
    public async Task<IActionResult> CheckForUpdatesNow(CancellationToken ct) => Ok(await updateCheck.CheckAsync(ct));

    public record RecommendationIndexResponse(
        bool ModelPresent, bool DumpPresent, int VectorCount, int? RecommendableTotal,
        bool Running, string Phase, int Embedded, int Scanned,
        DateTime? StartedAt, DateTime? FinishedAt, int LastEmbedded, string? LastError,
        int? EstimatedSecondsRemaining, bool PrebuiltEnabled, 
        DateTime? PrebuiltInstalledAt, string EmbeddingModel, 
        bool UseFullDump, bool ModelSwitching, string? ModelSwitchError);

    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendationIndex(CancellationToken ct)
    {
        var snap = embeddingStatus.Snapshot();
        var dumpPresent = (await mangaBakaDump.GetStatusAsync(ct)).Present;

        // The recommendable total needs a full-table count; compute it once when idle and
        // cache it on the status object so status polls stay cheap.
        var total = snap.RecommendableTotal;
        if (total is null && !snap.Running && dumpPresent)
        {
            total = await embeddingIndexer.CountRecommendableAsync(ct);
            embeddingStatus.SetTotal(total.Value);
        }

        var prebuiltEnabled = await prebuiltIndex.IsEnabledAsync(ct);
        var prebuiltInstalledAt =
            DateTime.TryParse(
                await settings.GetAsync(SettingKeys.RecommendationsPrebuiltGeneratedAt, ct),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var installedAt)
                ? installedAt
                : (DateTime?)null;

        return Ok(new RecommendationIndexResponse(
            embeddingModel.IsPresent(), dumpPresent, embeddingStore.Count(), total,
            snap.Running, snap.Phase, snap.Embedded, snap.Scanned,
            snap.StartedAt, snap.FinishedAt, snap.LastEmbedded, snap.LastError, 
            snap.EstimatedSecondsRemaining, prebuiltEnabled, prebuiltInstalledAt,
            modelSwitcher.CurrentModel,
            string.Equals(await settings.GetAsync(SettingKeys.MangaBakaUseFullDump, ct), "true", StringComparison.OrdinalIgnoreCase),
            modelSwitcher.Switching, modelSwitcher.LastError));
    }

    public record PrebuiltIndexRequest(bool Enabled);

    /// <summary>
    /// Toggles automatic installation of the published prebuilt index. On by default: the vectors
    /// are derived from the public MangaBaka dump, so downloading them is byte-for-byte equivalent
    /// to spending ~an hour of local CPU.
    /// </summary>
    [HttpPut("recommendations/prebuilt")]
    public async Task<IActionResult> SetPrebuiltIndexEnabled(
        [FromBody] PrebuiltIndexRequest request, CancellationToken ct)
    {
        await settings.SetAsync(
            SettingKeys.RecommendationsPrebuiltEnabled, request.Enabled ? "true" : "false", ct);
        return Ok(new { request.Enabled });
    }

    /// <summary>
    /// Downloads the prebuilt index now, ignoring the "is it newer" check but not the
    /// compatibility ones. Runs inline rather than through the scheduler so the UI can report
    /// exactly why an install was skipped.
    /// </summary>
    [HttpPost("recommendations/prebuilt/download")]
    public async Task<IActionResult> DownloadPrebuiltIndex(CancellationToken ct)
    {
        if (embeddingStatus.Running)
        {
            return Ok(new { installed = false, reason = "An indexing pass is running." });
        }

        var result = await prebuiltIndex.InstallAsync(force: true, ct);
        return Ok(new { installed = result.Installed, reason = result.Reason, rowCount = result.RowCount });
    }

    public record EmbeddingModelRequest(string Model);

    /// <summary>
    /// Switches the embedding model: "base" (default, ~240 MB RAM) or "large" (higher quality,
    /// ~500 MB RAM and a larger download). Applies live — no restart, no local re-index: the switch
    /// runs in the background, downloading the model's files and its prebuilt index, and the setting
    /// is persisted by the switcher when the switch actually starts. Poll the recommendations status
    /// (<c>modelSwitching</c>) for progress. A no-op when already on that model.
    /// </summary>
    [HttpPut("recommendations/model")]
    public IActionResult SetEmbeddingModel([FromBody] EmbeddingModelRequest request)
    {
        var result = modelSwitcher.Start(request.Model);
        return Ok(new { model = result.Model, switching = result.Started, reason = result.Reason });
    }

    public record FullDumpRequest(bool UseFullDump);

    /// <summary>
    /// Toggles downloading the larger "full" MangaBaka dump, which carries the MangaUpdates
    /// description the indexer prefers. Only useful on a machine that builds the index locally.
    /// </summary>
    [HttpPut("recommendations/fulldump")]
    public async Task<IActionResult> SetUseFullDump([FromBody] FullDumpRequest request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.MangaBakaUseFullDump, request.UseFullDump ? "true" : "false", ct);
        return Ok(new { request.UseFullDump });
    }

    [HttpPost("recommendations/build")]
    public async Task<IActionResult> BuildRecommendationIndex(CancellationToken ct)
    {
        if (embeddingStatus.Running)
        {
            return Ok(new { started = false, message = "Indexing is already running" });
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var data = new JobDataMap { { EmbeddingIndexJob.ManualTriggerKey, true } };
        await scheduler.TriggerJob(EmbeddingIndexJob.Key, data, ct);
        return Ok(new { started = true });
    }

    public record ScrobbleSettings(
        string? AniListClientId, string? AniListClientSecret,
        string? MalClientId, string? MalClientSecret,
        string? MangaBakaToken,
        string? KitsuClientId, string? KitsuClientSecret, string? KitsuEmail, string? KitsuPassword,
        int IntervalMinutes, bool PlanToRead, string? LibraryIds);

    [HttpGet("scrobble")]
    public async Task<IActionResult> GetScrobble(CancellationToken ct) => Ok(new ScrobbleSettings(
        await settings.GetAsync(SettingKeys.ScrobbleAniListClientId, ct),
        await settings.GetAsync(SettingKeys.ScrobbleAniListClientSecret, ct),
        await settings.GetAsync(SettingKeys.ScrobbleMalClientId, ct),
        await settings.GetAsync(SettingKeys.ScrobbleMalClientSecret, ct),
        await settings.GetAsync(SettingKeys.ScrobbleMangaBakaToken, ct),
        await settings.GetAsync(SettingKeys.ScrobbleKitsuClientId, ct),
        await settings.GetAsync(SettingKeys.ScrobbleKitsuClientSecret, ct),
        await settings.GetAsync(SettingKeys.ScrobbleKitsuEmail, ct),
        await settings.GetAsync(SettingKeys.ScrobbleKitsuPassword, ct),
        int.TryParse(await settings.GetAsync(SettingKeys.ScrobbleIntervalMinutes, ct), out var m) && m >= 5
            ? m
            : Services.ScrobbleService.DefaultIntervalMinutes,
        await settings.GetAsync(SettingKeys.ScrobblePlanToRead, ct) == "true",
        await settings.GetAsync(SettingKeys.ScrobbleLibraryIds, ct)));

    [HttpPut("scrobble")]
    public async Task<IActionResult> SetScrobble([FromBody] ScrobbleSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.ScrobbleAniListClientId, request.AniListClientId, ct);
        await settings.SetAsync(SettingKeys.ScrobbleAniListClientSecret, request.AniListClientSecret, ct);
        await settings.SetAsync(SettingKeys.ScrobbleMalClientId, request.MalClientId, ct);
        await settings.SetAsync(SettingKeys.ScrobbleMalClientSecret, request.MalClientSecret, ct);
        await settings.SetAsync(SettingKeys.ScrobbleMangaBakaToken, request.MangaBakaToken, ct);
        // Per Kitsu API documentation, Client ID and Secret is not yet implemented and these temp values should be used.
        await settings.SetAsync(SettingKeys.ScrobbleKitsuClientId, "dd031b32d2f56c990b1425efe6c42ad847e7fe3ab46bf1299f05ecd856bdb7dd", ct);
        await settings.SetAsync(SettingKeys.ScrobbleKitsuClientSecret, "54d7307928f63414defd96399fc31ba847961ceaecef3a5fd93144e960c0e151", ct);
        await settings.SetAsync(SettingKeys.ScrobbleKitsuEmail, request.KitsuEmail, ct);
        await settings.SetAsync(SettingKeys.ScrobbleKitsuPassword, request.KitsuPassword, ct);
        await settings.SetAsync(SettingKeys.ScrobbleIntervalMinutes,
            Math.Max(request.IntervalMinutes, 5).ToString(), ct);
        await settings.SetAsync(SettingKeys.ScrobblePlanToRead, request.PlanToRead ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.ScrobbleLibraryIds, request.LibraryIds, ct);
        return await GetScrobble(ct);
    }

    [HttpGet("general")]
    public IActionResult GetGeneral()
    {
        return Ok(new { apiKey = configFile.Config.ApiKey, port = configFile.Config.Port });
    }
}
