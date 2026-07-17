namespace Mangarr.Api.Configuration;

/// <summary>
/// Resolves where Mangarr keeps its state. Order: MANGARR_CONFIG_DIR env var,
/// /config when it exists (Docker convention), else a per-user app-data folder.
/// </summary>
public class AppPaths
{
    public AppPaths()
    {
        var configured = Environment.GetEnvironmentVariable("MANGARR_CONFIG_DIR");
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
                "Mangarr");
        }

        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(MediaCoverDir);
        Directory.CreateDirectory(BackupDir);
    }

    public string ConfigDir { get; }
    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string DatabasePath => Path.Combine(ConfigDir, "mangarr.db");
    public string MangaBakaDbPath => Path.Combine(ConfigDir, "mangabaka.db");
    public string EmbeddingsDbPath => Path.Combine(ConfigDir, "embeddings.db");
    public string ModelsDir => Path.Combine(ConfigDir, "models", "bge-small-en-v1.5");
    public string LogDir => Path.Combine(ConfigDir, "logs");
    public string CacheDir => Path.Combine(ConfigDir, "cache");
    public string DownloadCacheDir => Path.Combine(CacheDir, "downloads");
    public string MediaCoverDir => Path.Combine(ConfigDir, "MediaCover");
    public string BackupDir => Path.Combine(ConfigDir, "backups");

    /// <summary>Staging dir for a restore pending on next boot. Applied (and cleared) at startup
    /// before anything reads config.json or opens the DB. See <c>RestoreBootstrap</c>.</summary>
    public string RestorePendingDir => Path.Combine(ConfigDir, "restore-pending");
}
