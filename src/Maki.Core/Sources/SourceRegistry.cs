namespace Maki.Core.Sources;

/// <summary>
/// Lookup over all registered ISource implementations plus whatever the <see cref="ISourceProvider"/>s
/// currently supply. Readers see an immutable snapshot that <see cref="ReloadAsync"/> swaps whole, so
/// nothing ever observes half a reload.
/// </summary>
public class SourceRegistry
{
    private readonly IReadOnlyList<ISource> _static;
    private readonly IReadOnlyList<ISourceProvider> _providers;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private Dictionary<string, ISource> _byName;

    public SourceRegistry(IEnumerable<ISource> sources) : this(sources, []) { }

    public SourceRegistry(IEnumerable<ISource> sources, IEnumerable<ISourceProvider> providers)
    {
        _static = [.. sources];
        _providers = [.. providers];
        _byName = Build();
    }

    public IReadOnlyCollection<ISource> All => Volatile.Read(ref _byName).Values;

    public ISource? Find(string name) => Volatile.Read(ref _byName).GetValueOrDefault(name);

    public ISource GetRequired(string name) =>
        Find(name) ?? throw new InvalidOperationException($"Unknown source: {name}");

    /// <summary>
    /// Reloads every provider and swaps in the new snapshot. Nothing notifies the registry of a
    /// settings change, so whatever writes a provider's persisted state calls this afterwards.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _reloadGate.WaitAsync(ct);
        try
        {
            foreach (var provider in _providers)
            {
                await provider.ReloadAsync(ct);
            }

            Volatile.Write(ref _byName, Build());
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Registered sources first and in registration order, which <c>SourceMatchService.OrderSources</c>
    /// falls back to for anything the priority list doesn't name. A provider source never replaces a
    /// registered one of the same name.
    /// </summary>
    private Dictionary<string, ISource> Build()
    {
        var byName = _static.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var source in _providers.SelectMany(p => p.Current))
        {
            byName.TryAdd(source.Name, source);
        }

        return byName;
    }
}
