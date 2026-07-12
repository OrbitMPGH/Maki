namespace Mangarr.Metadata.MangaBaka;

/// <summary>
/// Where the local MangaBaka database dump lives. Constructed from AppPaths in the
/// API host (Mangarr.Metadata cannot reference Mangarr.Api).
/// </summary>
public record MangaBakaDumpOptions(string DatabasePath, string StagingDirectory);
