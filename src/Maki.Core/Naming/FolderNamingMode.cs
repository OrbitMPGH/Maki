namespace Maki.Core.Naming;

/// <summary>
/// Controls whether an imported series' on-disk folder is renamed to Maki's sanitized-title
/// standard, and which folder name future chapter downloads for that series use.
/// </summary>
public static class FolderNamingMode
{
    /// <summary>Rename the on-disk folder to the sanitized title; downloads follow it. Default.</summary>
    public const string Rename = "rename";

    /// <summary>Leave the on-disk folder as-is; new chapter downloads go into a separate,
    /// standard-named folder instead.</summary>
    public const string KeepOriginalNewStandard = "keep-new-standard";

    /// <summary>Leave the on-disk folder as-is; new chapter downloads go into it too.</summary>
    public const string KeepOriginal = "keep-original";

    public const string Default = Rename;

    public static readonly string[] All = [Rename, KeepOriginalNewStandard, KeepOriginal];

    public static bool IsValid(string? mode) => mode is not null && All.Contains(mode);
}
