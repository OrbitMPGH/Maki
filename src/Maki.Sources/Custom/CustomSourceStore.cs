using System.Text.Json;
using System.Text.Json.Nodes;
using Maki.Core.Configuration;
using Maki.Core.Sources.Custom;

namespace Maki.Sources.Custom;

/// <summary>
/// One stored entry of <see cref="SettingKeys.CustomSources"/>: the JSON as saved, plus the
/// definition when this build can read it, or why not.
/// </summary>
public sealed record StoredCustomSource(string? Id, JsonObject Raw, CustomSourceDefinition? Definition, string? Problem);

/// <summary>
/// The only reader and writer of <see cref="SettingKeys.CustomSources"/>. Entries are kept verbatim,
/// so a definition this build can't read (a newer schema) is not lost when another one is saved.
/// Writes are serialised; whoever writes reloads the source registry afterwards.
/// </summary>
public sealed class CustomSourceStore(IAppSettings settings)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<StoredCustomSource>> ReadAsync(CancellationToken ct = default) =>
        Read(await settings.GetAsync(SettingKeys.CustomSources, ct)).Select(Describe).ToList();

    /// <summary>Read-modify-write under one lock. <paramref name="mutate"/> returns false to write nothing.</summary>
    public async Task<T> UpdateAsync<T>(Func<List<JsonObject>, (bool Changed, T Result)> mutate, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entries = Read(await settings.GetAsync(SettingKeys.CustomSources, ct), forWrite: true);
            var (changed, result) = mutate(entries);
            if (changed)
            {
                await settings.SetAsync(SettingKeys.CustomSources, new JsonArray([.. entries]).ToJsonString(), ct);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string? IdOf(JsonObject raw) =>
        raw.TryGetPropertyValue("id", out var id) && id is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static JsonObject ToJson(CustomSourceDefinition definition) =>
        JsonNode.Parse(definition.ToJson())!.AsObject();

    private static StoredCustomSource Describe(JsonObject raw)
    {
        var id = IdOf(raw);
        try
        {
            var definition = CustomSourceDefinition.Parse(raw.ToJsonString());
            var problems = CustomSourceValidator.Validate(definition);
            return problems.Count == 0
                ? new StoredCustomSource(id, raw, definition, null)
                : new StoredCustomSource(id, raw, null, string.Join("; ", problems));
        }
        catch (JsonException ex)
        {
            return new StoredCustomSource(id, raw, null, ex.Message);
        }
    }

    /// <summary>
    /// A stored value that doesn't parse reads as no sources, but a write refuses it: writing the
    /// one entry being saved over it would throw every other definition away.
    /// </summary>
    private static List<JsonObject> Read(string? json, bool forWrite = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(json) is JsonArray array)
            {
                return array.OfType<JsonObject>().Select(o => o.DeepClone().AsObject()).ToList();
            }
        }
        catch (JsonException)
        {
        }

        return forWrite
            ? throw new InvalidOperationException($"The stored {SettingKeys.CustomSources} value doesn't parse; refusing to overwrite it")
            : [];
    }
}
