namespace Mangarr.Core.Sources;

/// <summary>Lookup over all registered ISource implementations.</summary>
public class SourceRegistry(IEnumerable<ISource> sources)
{
    private readonly Dictionary<string, ISource> _byName =
        sources.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ISource> All => _byName.Values;

    public ISource? Find(string name) => _byName.GetValueOrDefault(name);

    public ISource GetRequired(string name) =>
        Find(name) ?? throw new InvalidOperationException($"Unknown source: {name}");
}
