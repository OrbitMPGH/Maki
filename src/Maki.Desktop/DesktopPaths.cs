using System.Text.Json;

namespace Maki.Desktop;

internal static class DesktopPaths
{
    /// <summary>
    /// Maki's data, kept out of the install folder: Velopack deletes that folder on uninstall, and
    /// out of the roaming profile, which some setups copy on every sign-in.
    /// </summary>
    public static string ConfigDir { get; } =
        Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maki");

    public static string LogDir => Path.Combine(ConfigDir, "logs");

    /// <summary>Must match <c>HostRuntime.UpdateReadyFile</c> in Maki.Api.</summary>
    public static string UpdateReadyFile => Path.Combine(ConfigDir, "update-ready");

    /// <summary><c>server\Maki.Api.exe</c> beside the tray, or <c>MAKI_DESKTOP_SERVER</c> when
    /// running from a development build.</summary>
    public static string ServerExecutable =>
        Environment.GetEnvironmentVariable("MAKI_DESKTOP_SERVER") is { Length: > 0 } dev
            ? dev
            : Path.Combine(AppContext.BaseDirectory, "server", "Maki.Api.exe");

    /// <summary>The port in config.json, which Maki writes on its first start.</summary>
    public static int Port()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ConfigDir, "config.json")));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("Port", StringComparison.OrdinalIgnoreCase) && property.Value.TryGetInt32(out var port))
                    return port;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return 8990;
    }
}
