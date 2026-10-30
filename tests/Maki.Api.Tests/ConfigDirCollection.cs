namespace Maki.Api.Tests;

/// <summary>
/// A host booted through WebApplicationFactory finds its config dir the way production does, from
/// the process-global MAKI_CONFIG_DIR, and each class that boots one points the variable at its
/// own temp directory and deletes it on dispose. xUnit runs test classes in parallel, so without
/// this collection one class can retarget or delete the directory while another host is starting
/// on it (a DirectoryNotFoundException on config.json, or an empty route collection). Sharing a
/// collection serialises them.
///
/// Everything else passes its own directory to <c>new AppPaths(dir)</c> and stays out of here.
/// Only classes that set MAKI_CONFIG_DIR belong, plus the few that change other process-wide state
/// a booting host reads. <see cref="ConfigDirCollectionUsageTests"/> checks both rules.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConfigDirCollection
{
    public const string Name = "config-dir";
}
