namespace Maki.Core.Entities;

/// <summary>Generic key/value settings store (source toggles, FlareSolverr URL, ...).</summary>
public class AppConfigEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
