namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Where the local MangaBaka database dump lives. Constructed from AppPaths in the
/// API host (Maki.Metadata cannot reference Maki.Api).
/// <para>
/// <paramref name="Shared"/> means another instance owns the file: this one only reads it, and
/// keeps what it derives from it (the catalogue snapshot) in <paramref name="SnapshotDirectory"/>
/// rather than beside the dump.
/// </para>
/// </summary>
public record MangaBakaDumpOptions(
    string DatabasePath, string StagingDirectory, bool Shared = false, string? SnapshotDirectory = null);
