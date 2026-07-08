namespace Mangarr.Core.Configuration;

/// <summary>Read access to the key/value settings store (implemented over the DB in Mangarr.Api).</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
}

public static class SettingKeys
{
    public const string FlareSolverrUrl = "flaresolverr.url";
}
