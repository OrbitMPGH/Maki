using System.Text.Json;

namespace Maki.Api.Configuration;

public class ConfigFile
{
    public int Port { get; set; } = 8990;

    /// <summary>
    /// Where Kestrel listens: <c>*</c> for every network, <c>localhost</c> for this machine only, or
    /// one IP address. The desktop build starts on <c>localhost</c> because a laptop on public Wi-Fi
    /// should not offer Maki to everyone on it; inside a container only <c>*</c> is reachable.
    /// </summary>
    public string BindAddress { get; set; } = HostRuntime.Kind == HostRuntimeKind.Desktop ? "localhost" : "*";

    public string LogLevel { get; set; } = "Information";

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

    /// <summary>
    /// Also write every request to <c>logs/access-*.log</c> in the combined log format, for
    /// CrowdSec or fail2ban. See <c>Maki.Api.Logging.AccessLogMiddleware</c>.
    /// </summary>
    public bool AccessLog { get; set; }
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
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public ConfigFileProvider(AppPaths paths)
    {
        this.paths = paths;

        var raw = File.Exists(paths.ConfigFile) ? File.ReadAllText(paths.ConfigFile) : null;
        try
        {
            Config = raw is null
                ? new ConfigFile()
                : JsonSerializer.Deserialize<ConfigFile>(raw, ReadOptions) ?? new ConfigFile();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{paths.ConfigFile} is not valid JSON: {ex.Message}", ex);
        }

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
            Write(Config);
        }
    }

    /// <summary>
    /// Changes config.json on disk for the next start. <see cref="Config"/> keeps describing what this
    /// process is running with, since nothing in this file is re-read while it runs.
    /// </summary>
    public ConfigFile UpdateOnDisk(Action<ConfigFile> change)
    {
        var onDisk = ReadOnDisk();
        change(onDisk);
        Write(onDisk);
        return onDisk;
    }

    public ConfigFile ReadOnDisk() =>
        File.Exists(paths.ConfigFile)
            ? JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(paths.ConfigFile), ReadOptions) ?? new ConfigFile()
            : new ConfigFile();

    /// <summary>The URL Kestrel listens on. Throws on a bind address that is not <c>*</c>,
    /// <c>localhost</c> or an IP address, so a typo stops startup with a reason instead of binding
    /// somewhere unexpected.</summary>
    public static string ListenUrl(ConfigFile config)
    {
        var address = config.BindAddress.Trim();
        if (address is "*" or "" || address.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return $"http://{(address is "" ? "*" : address.ToLowerInvariant())}:{config.Port}";

        if (!System.Net.IPAddress.TryParse(address, out var ip))
            throw new InvalidOperationException(
                $"BindAddress \"{config.BindAddress}\" in config.json must be *, localhost or an IP address.");

        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"http://[{ip}]:{config.Port}"
            : $"http://{ip}:{config.Port}";
    }

    private void Write(ConfigFile config)
    {
        // Written beside the file and moved over it, so a crash mid-write cannot leave an empty config.json.
        var temp = paths.ConfigFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, paths.ConfigFile, overwrite: true);
    }

    private static bool IsMissingKeys(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var present = document.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return typeof(ConfigFile).GetProperties().Any(property => !present.Contains(property.Name));
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
