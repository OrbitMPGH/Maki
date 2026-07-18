using System.Reflection;

namespace Maki.Api;

/// <summary>
/// The running build's version, read from AssemblyInformationalVersion.
/// </summary>
/// <remarks>
/// Not <c>Assembly.GetName().Version</c>: that is the four-part numeric AssemblyVersion, which
/// cannot carry a SemVer prerelease label — a 0.9.0-beta.1 build reports itself as plain "0.9.0"
/// there, indistinguishable from the real release. InformationalVersion is a free-form string and
/// keeps the whole thing, in the "1.2.3-beta.1+abcdef0" shape CI stamps.
/// </remarks>
public static class VersionInfo
{
    static VersionInfo()
    {
        var informational = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            Version = "0.0.0-dev";
            Commit = null;
            return;
        }

        // The SDK appends "+<sha>" of its own accord when SourceLink is in play, so split on the
        // first '+' rather than assuming CI is the only writer of build metadata.
        var plus = informational.IndexOf('+');
        Version = plus < 0 ? informational : informational[..plus];
        Commit = plus < 0 || plus == informational.Length - 1 ? null : informational[(plus + 1)..];
    }

    /// <summary>SemVer version without build metadata, e.g. "0.9.0" or "0.9.1-beta.2".</summary>
    public static string Version { get; }

    /// <summary>Commit the build came from, or null for a local build.</summary>
    public static string? Commit { get; }

    /// <summary>True when this is a local build rather than one CI stamped a version onto.</summary>
    public static bool IsDevBuild => Version.EndsWith("-dev", StringComparison.Ordinal);
}
