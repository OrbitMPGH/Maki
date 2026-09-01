using System.Text.Json;

namespace Maki.Api.Configuration;

public class ConfigFile
{
    public int Port { get; set; } = 8990;
    public string LogLevel { get; set; } = "Information";
    public string UrlBase { get; set; } = string.Empty;

    /// <summary>
    /// <c>Off</c>, <c>Minimal</c> or <c>Full</c>. See <c>Maki.Api.Logging.HttpRequestLogMode</c>.
    /// </summary>
    public string HttpRequestLogging { get; set; } = "Minimal";

    /// <summary>A request slower than this is logged as a warning whatever else it did.</summary>
    public int SlowRequestMs { get; set; } = 1000;

    /// <summary>Size cap per log file. Files roll on the day boundary and on this, whichever first.</summary>
    public int LogFileSizeMb { get; set; } = 20;

    /// <summary>How many rolled files to keep. Counts size-rolled segments, not days.</summary>
    public int RetainedLogFiles { get; set; } = 7;
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
        //
        // Also rewrite when a release has added a key the file predates. This file is the only place
        // several settings can be changed from, and an operator cannot set a key they have never
        // seen; writing the full shape back keeps it self-documenting. The values written are the
        // ones already in effect, so this never changes behaviour.
        if (raw is null || raw.Contains("\"apiKey\"", StringComparison.OrdinalIgnoreCase) || IsMissingKeys(raw))
        {
            File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(Config, JsonOptions));
        }
    }

    private static bool IsMissingKeys(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            return typeof(ConfigFile).GetProperties()
                .Any(property => !document.RootElement.TryGetProperty(property.Name, out _));
        }
        catch (JsonException)
        {
            // Unparseable on disk. Deserialize would already have thrown above, so this is only
            // reachable if that ever becomes tolerant; either way, do not overwrite what we cannot read.
            return false;
        }
    }

    public ConfigFile Config { get; }

    private readonly AppPaths paths;
}
