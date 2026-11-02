using System.Text.Json;

namespace Maki.Core.Sources.Custom;

/// <summary>
/// A URL serving a <see cref="CustomRepositoryIndex"/>, stored under
/// <see cref="Configuration.SettingKeys.CustomRepositories"/> with the index it last answered.
/// </summary>
public sealed record CustomRepository
{
    public string Url { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Apply updates without asking when they pass the checks. Only for https repositories.</summary>
    public bool AutoUpdate { get; init; }
    public DateTime? LastCheckedUtc { get; init; }

    /// <summary>The last refresh's failure, or an update that waits for the admin and why.</summary>
    public CustomRepositoryError? LastError { get; init; }
    public CustomRepositoryIndex? Index { get; init; }

    public bool Secure => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>A catalogue key and its values, worded when an admin reads it.</summary>
public sealed record CustomRepositoryError(string Key, Dictionary<string, string>? Args = null);

/// <summary>What a repository serves. Fields this build doesn't know are ignored, so authors can add their own.</summary>
public sealed record CustomRepositoryIndex
{
    public string Name { get; init; } = "";
    public List<CustomRepositoryEntry> Definitions { get; init; } = [];

    public const int MaxBytes = 256 * 1024;
}

public sealed record CustomRepositoryEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Language { get; init; } = "en";
    public int Schema { get; init; } = CustomSourceDefinition.CurrentSchema;
    public int Version { get; init; }

    /// <summary>The definition file, relative to the index's own URL or absolute.</summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// Hex SHA-256 of the file. It comes from the same server, so it catches a broken upload,
    /// not tampering.
    /// </summary>
    public string? Sha256 { get; init; }
}
