using Maki.Core.Configuration;

namespace Maki.Metadata.Tests;

/// <summary>In-memory IAppSettings for tests.</summary>
public class FakeAppSettings : IAppSettings
{
    public Dictionary<string, string?> Values { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
}
