using System.Text.Json;

namespace Maki.Api.Configuration;

public class ConfigFile
{
    public int Port { get; set; } = 8990;
    public string LogLevel { get; set; } = "Information";
    public string UrlBase { get; set; } = string.Empty;
}

/// <summary>
/// Loads <c>{ConfigDir}/config.json</c>, writing a default one on first run.
/// <para>
/// This file no longer holds a credential. It used to carry a single instance-wide <c>apiKey</c> that
/// authenticated every request, which the SPA obtained from an anonymous endpoint — so the credential
/// was readable by anyone who could reach the page it protected. Credentials now belong to user
/// accounts, live in the database as SHA-256 digests, and are created under Account.
/// </para>
/// </summary>
public class ConfigFileProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigFileProvider(AppPaths paths)
    {
        this.paths = paths;

        var raw = File.Exists(paths.ConfigFile) ? File.ReadAllText(paths.ConfigFile) : null;
        Config = raw is null
            ? new ConfigFile()
            : JsonSerializer.Deserialize<ConfigFile>(raw) ?? new ConfigFile();

        // Rewrite whenever the file is missing or still carries the retired apiKey. Leaving a dead
        // secret behind in a config file is how someone later concludes it still works and treats a
        // leak of it as harmless — or worse, as harmful when it isn't. It authenticates nothing now,
        // so the honest thing is to drop it from disk on first start after the upgrade.
        if (raw is null || raw.Contains("\"apiKey\"", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(Config, JsonOptions));
        }
    }

    public ConfigFile Config { get; }

    private readonly AppPaths paths;
}
