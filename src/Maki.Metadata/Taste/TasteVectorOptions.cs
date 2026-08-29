namespace Maki.Metadata.Taste;

/// <summary>
/// Where the behavioural-vector artifact lives. Mirrors <c>RecoGraphOptions</c> and
/// <c>CoReadOptions</c>: an absent file is the normal state of an install, not an error.
/// </summary>
public sealed record TasteVectorOptions(string DatabasePath, string StagingDirectory);
