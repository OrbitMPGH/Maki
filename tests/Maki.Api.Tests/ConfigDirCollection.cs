namespace Maki.Api.Tests;

/// <summary>
/// MAKI_CONFIG_DIR is process-global, and every class that points it at its own temp directory
/// deletes that directory on dispose. xUnit parallelises test classes within an assembly, so
/// without this collection one class can retarget or delete the variable's directory while
/// another is reading it — most visibly while HostStartupTests boots a host through
/// WebApplicationFactory, which fails as a DirectoryNotFoundException on config.json or as an
/// empty route collection. Sharing a collection serialises them instead.
///
/// Every class that sets MAKI_CONFIG_DIR, or reads it via AppPaths / WebApplicationFactory,
/// belongs here.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConfigDirCollection
{
    public const string Name = "config-dir";
}
