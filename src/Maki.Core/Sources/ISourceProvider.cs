namespace Maki.Core.Sources;

/// <summary>
/// Sources that are not known at build time, such as the ones an admin picked from an external
/// server's catalogue. <see cref="SourceRegistry"/> merges them with the registered
/// <see cref="ISource"/> implementations.
/// <para>
/// <see cref="Current"/> must not depend on the external server being reachable: build it from
/// persisted state, so a server that is down shows up as failing sources rather than missing ones
/// (a missing source's mappings are silently skipped, and the priority endpoint refuses its name).
/// </para>
/// </summary>
public interface ISourceProvider
{
    IReadOnlyList<ISource> Current { get; }

    /// <summary>Re-reads the persisted state behind <see cref="Current"/>. Called by <see cref="SourceRegistry.ReloadAsync"/>.</summary>
    Task ReloadAsync(CancellationToken ct = default);
}
