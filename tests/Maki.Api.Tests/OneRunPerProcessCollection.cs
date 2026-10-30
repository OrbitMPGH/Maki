namespace Maki.Api.Tests;

/// <summary>
/// UpgradeScanService, UpgradeVolumeSearchJob and HealthMonitor each allow one run per process
/// through a static gate, and a second caller is turned away (a scan answers "already running", a
/// health refresh quietly does nothing) rather than waiting. Classes that start any of them share
/// this collection so they never meet another class's run. <see cref="ConfigDirCollectionUsageTests"/>
/// checks that they do.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OneRunPerProcessCollection
{
    public const string Name = "one-run-per-process";
}
