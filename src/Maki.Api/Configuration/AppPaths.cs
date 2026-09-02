using Maki.Metadata.Embedding;

namespace Maki.Api.Configuration;

/// <summary>
/// Resolves where Maki keeps its state. Order: MAKI_CONFIG_DIR env var,
/// /config when it exists (Docker convention), else a per-user app-data folder.
/// </summary>
public class AppPaths
{
    public AppPaths()
    {
        var configured = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ConfigDir = configured;
        }
        else if (Directory.Exists("/config"))
        {
            ConfigDir = "/config";
        }
        else
        {
            ConfigDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Maki");
        }

        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataProtectionKeysDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(MediaCoverDir);
        Directory.CreateDirectory(BackupDir);
    }

    public string ConfigDir { get; }
    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string DatabasePath => Path.Combine(ConfigDir, "maki.db");
    public string MangaBakaDbPath => Path.Combine(ConfigDir, "mangabaka.db");
    public string EmbeddingsDbPath => Path.Combine(ConfigDir, "embeddings.db");

    /// <summary>
    /// The co-recommendation edge graph. This is the folded <c>pair</c> artifact that
    /// <c>distribution/fetch-reco-graph.cs</c> exports, never its <c>reco-graph.db</c> working file.
    /// Optional: absent simply means the channel contributes nothing.
    /// </summary>
    public string RecoGraphDbPath => Path.Combine(ConfigDir, "reco-edges.db");

    /// <summary>
    /// The co-read edge artifact. Never <c>coread-graph.db</c>, which is the fetcher's working
    /// state and holds per-user reading data.
    /// </summary>
    public string CoReadDbPath => Path.Combine(ConfigDir, "coread-edges.db");

    /// <summary>
    /// The behavioural item vectors. Like both graph artifacts, absent is the normal state and the
    /// channel simply contributes nothing; unlike them this is a factor matrix rather than a pair
    /// table, so it is the vector index that loads it and the index that has to be invalidated when
    /// it changes.
    /// </summary>
    public string TasteVectorsDbPath => Path.Combine(ConfigDir, "taste-vectors.db");

    /// <summary>
    /// The reader-cohort aggregates. Never <c>coread-graph.db</c>, which is the fetcher's working
    /// state and holds per-user reading data; this file has no user axis at all. Absent is the
    /// normal state: the hint does not render, the rail returns nothing and the taste page falls
    /// back to its popularity proxy.
    /// </summary>
    public string ReaderCohortsDbPath => Path.Combine(ConfigDir, "reader-cohorts.db");

    /// <summary>Root for embedding models; each model profile installs in its own subfolder.</summary>
    public string ModelsDir => Path.Combine(ConfigDir, "models");
    public string LogDir => Path.Combine(ConfigDir, "logs");
    public string CacheDir => Path.Combine(ConfigDir, "cache");
    public string DownloadCacheDir => Path.Combine(CacheDir, "downloads");
    /// <summary>Reader page thumbnails, one folder per ChapterFile. Regenerable; swept by HousekeepingJob.</summary>
    public string ReaderCacheDir => Path.Combine(CacheDir, "reader");

    /// <summary>
    /// Sample pages fetched for the source-comparison view, <c>{seriesId}/{sourceName}/000.jpg</c>.
    /// Throwaway: a job wipes its own series folder before refilling it, and HousekeepingJob drops
    /// anything left behind.
    /// </summary>
    public string SourcePreviewDir => Path.Combine(CacheDir, "sourcepreview");
    public string MediaCoverDir => Path.Combine(ConfigDir, "MediaCover");
    public string BackupDir => Path.Combine(ConfigDir, "backups");

    /// <summary>
    /// ASP.NET data protection key ring — what signs session cookies and antiforgery tokens.
    /// <para>
    /// Kept here rather than at the framework default (<c>%LOCALAPPDATA%</c>, or a container path
    /// that does not survive recreation) so sessions live through restarts and upgrades. This is
    /// <b>credential material</b>: whoever holds these keys can mint a session cookie for any user,
    /// so it sits behind the same filesystem-permission boundary as <c>maki.db</c> and is
    /// deliberately excluded from backups.
    /// </para>
    /// </summary>
    public string DataProtectionKeysDir => Path.Combine(ConfigDir, "dataprotection-keys");

    /// <summary>Staging dir for a restore pending on next boot. Applied (and cleared) at startup
    /// before anything reads config.json or opens the DB. See <c>RestoreBootstrap</c>.</summary>
    public string RestorePendingDir => Path.Combine(ConfigDir, "restore-pending");
}
