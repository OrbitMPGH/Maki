namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Where the local MangaBaka database dump lives. Constructed from AppPaths in the
/// API host (Maki.Metadata cannot reference Maki.Api).
/// </summary>
public record MangaBakaDumpOptions(string DatabasePath, string StagingDirectory);
