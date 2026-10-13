namespace Maki.Api.Services;

/// <summary>
/// Left in the config dir when <c>Migrate()</c> throws, so the health page can say what went wrong
/// on a later boot that got through. It expires rather than lingering, since the failure it records
/// is over once the app is running.
/// </summary>
public static class MigrationErrorMarker
{
    public const string FileName = "health-migration-error.txt";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private const int MaxErrorLength = 400;

    public static void Write(string configDir, Exception ex)
    {
        try
        {
            var error = $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" ");
            if (error.Length > MaxErrorLength) error = error[..MaxErrorLength];
            File.WriteAllText(Path.Combine(configDir, FileName), $"{DateTime.UtcNow:O}\n{error}");
        }
        catch
        {
        }
    }

    /// <summary>Whether an unexpired marker exists, even one written before it carried a detail.</summary>
    public static bool Exists(string configDir, DateTime nowUtc)
    {
        var path = Path.Combine(configDir, FileName);
        try
        {
            if (!File.Exists(path)) return false;
            if (nowUtc - File.GetLastWriteTimeUtc(path) <= Lifetime) return true;
            File.Delete(path);
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The recorded error, or null when there is none, it has expired (and is then removed), or it carries no detail.</summary>
    public static string? Read(string configDir, DateTime nowUtc)
    {
        var path = Path.Combine(configDir, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            if (nowUtc - File.GetLastWriteTimeUtc(path) > Lifetime)
            {
                File.Delete(path);
                return null;
            }

            var lines = File.ReadAllText(path).Split('\n', 2);
            return lines.Length > 1 && lines[1].Trim().Length > 0 ? lines[1].Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
