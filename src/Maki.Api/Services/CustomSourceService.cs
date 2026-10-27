using System.Text.Json;
using System.Text.Json.Nodes;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Data;
using Maki.Sources.Custom;
using Maki.Sources.Suwayomi;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Saving, testing and deleting custom source definitions. Everything that writes goes through
/// <see cref="CustomSourceStore"/> and then reloads the registry and drops the source's cached
/// listings, so nothing resolves a chapter against a listing the old definition made.
/// </summary>
public sealed class CustomSourceService(
    CustomSourceStore store,
    CustomSourceFactory factory,
    SourceRegistry registry,
    SourceChapterListCache listings,
    MakiDbContext db)
{
    public record Saved(CustomSourceDefinition Definition, bool Created);

    /// <param name="Reasons">Why the change could re-key chapters: <c>language</c>, <c>host</c>, <c>chapters</c>, <c>imageHosts</c> or <c>unverified</c>.</param>
    public record NeedsConfirmation(IReadOnlyList<string> Reasons, int LinkedSeries, IReadOnlyList<ChapterChange> Changes);

    public record ChapterChange(string? Before, string? After);

    public record Refused(IReadOnlyList<CustomSourceProblem> Problems);

    public async Task<IReadOnlyList<StoredCustomSource>> ListAsync(CancellationToken ct) => await store.ReadAsync(ct);

    public async Task<StoredCustomSource?> FindAsync(string id, CancellationToken ct) =>
        (await store.ReadAsync(ct)).FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses a definition sent as JSON, refusing it whole when it is too big or does not parse.</summary>
    public static (CustomSourceDefinition? Definition, CustomSourceProblem? Problem) Parse(JsonElement body)
    {
        // Where it came from is the server's to set and not part of the definition, so it neither
        // counts against the size limit (an updated entry carries its whole previous version) nor
        // gets read.
        var raw = body.GetRawText();
        if (body.ValueKind == JsonValueKind.Object && JsonNode.Parse(raw) is JsonObject node)
        {
            node.Remove("origin");
            node.Remove("previous");
            node.Remove("detached");
            raw = node.ToJsonString();
        }

        if (raw.Length > CustomSourceValidator.MaxDefinitionBytes)
        {
            return (null, new CustomSourceProblem("error.customSource.tooLarge", new { max = CustomSourceValidator.MaxDefinitionBytes / 1024 }));
        }

        try
        {
            return (CustomSourceDefinition.Parse(raw), null);
        }
        catch (JsonException ex)
        {
            return (null, new CustomSourceProblem("error.customSource.jsonInvalid", new { detail = ex.Message }));
        }
    }

    /// <summary>The registered source (not Suwayomi's) that reads this site, if any.</summary>
    public ISource? SourceForHost(string baseUrl) =>
        CustomSourceValidator.BaseUri(baseUrl) is { } baseUri
            ? registry.All.FirstOrDefault(s => s is not SuwayomiSource
                && Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var other) && CustomHost.Same(other.Host, baseUri.Host))
            : null;

    /// <summary>The validator's checks plus the ones that need the registry: one source per host.</summary>
    public List<CustomSourceProblem> Validate(CustomSourceDefinition definition)
    {
        var problems = CustomSourceValidator.Validate(definition);
        if (CustomSourceValidator.BaseUri(definition.BaseUrl) is not { } baseUri)
        {
            return problems;
        }

        foreach (var source in registry.All)
        {
            if (source is SuwayomiSource || string.Equals(source.Name, definition.SourceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var other) && CustomHost.Same(other.Host, baseUri.Host))
            {
                problems.Add(new CustomSourceProblem("error.customSource.hostTaken", new { source = source.DisplayName }));
            }
        }

        return problems;
    }

    /// <param name="fromRepository">
    /// The definition's <c>origin</c>, <c>previous</c> and <c>detached</c> are the caller's (an
    /// install, update or revert). Otherwise it is a hand edit: those three come from the stored
    /// entry, and an entry installed from a repository is detached, so its updates stop applying.
    /// </param>
    /// <param name="expected">
    /// The stored entry the caller decided on. When given, the save goes ahead only if that is still
    /// what is stored, checked under the store's lock, so an edit made while an update was being
    /// tested is never overwritten by it.
    /// </param>
    public async Task<object> SaveAsync(
        CustomSourceDefinition definition, bool confirmed, CancellationToken ct, bool fromRepository = false, JsonObject? expected = null)
    {
        var existing = await FindAsync(definition.Id, ct);
        if (!fromRepository)
        {
            var origin = existing is null ? null : OriginOf(existing);
            definition = definition with
            {
                Origin = origin,
                Detached = origin is not null,
                Previous = existing is null ? null : PreviousOf(existing)
            };
        }

        var problems = Validate(definition);
        if (problems.Count > 0)
        {
            return new Refused(problems);
        }

        // A stored entry this build can't read can't be compared either: with series linked to it, say so.
        if (!confirmed && existing is { Definition: null } && await LinkedSeriesAsync(definition.SourceName, ct) is var linkedToInvalid and > 0)
        {
            return new NeedsConfirmation(["unverified"], linkedToInvalid, []);
        }

        if (!confirmed && existing?.Definition is { } before && IdentityMayChange(before, definition))
        {
            var linked = await LinkedSeriesAsync(definition.SourceName, ct);
            if (linked > 0 && await CompareAsync(before, definition, ct) is { Reasons.Count: > 0 } diff)
            {
                return diff with { LinkedSeries = linked };
            }
        }

        var created = await store.UpdateAsync<bool?>(entries =>
        {
            var json = CustomSourceStore.ToJson(definition);
            var index = entries.FindIndex(e => string.Equals(CustomSourceStore.IdOf(e), definition.Id, StringComparison.OrdinalIgnoreCase));
            if (expected is not null && (index < 0 || !JsonNode.DeepEquals(entries[index], expected)))
            {
                return (false, null);
            }

            if (index >= 0)
            {
                entries[index] = json;
                return (true, false);
            }

            entries.Add(json);
            return (true, true);
        }, ct);

        if (created is not { } isNew)
        {
            return new Refused([new CustomSourceProblem("error.customSource.changedMeanwhile", new { id = definition.Id })]);
        }

        await AfterWriteAsync(definition.SourceName, ct);
        return new Saved(definition, isNew);
    }

    /// <summary>Removes the definition. Mappings stay, so importing the same id later brings them back.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        var removed = await store.UpdateAsync(entries =>
        {
            var count = entries.RemoveAll(e => string.Equals(CustomSourceStore.IdOf(e), id, StringComparison.OrdinalIgnoreCase));
            return (count > 0, count > 0);
        }, ct);

        if (!removed)
        {
            return false;
        }

        var name = CustomSourceDefinition.NamePrefix + id;

        // A deleted source is not "disabled", so the health check would keep reporting these errors for good.
        await db.SourceMappings
            .Where(m => m.SourceName == name && m.LastError != null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastError, (string?)null), ct);

        await AfterWriteAsync(name, ct, deleted: true);
        return true;
    }

    public Task<int> LinkedSeriesAsync(string sourceName, CancellationToken ct) =>
        db.SourceMappings.Where(m => m.SourceName == sourceName).Select(m => m.SeriesId).Distinct().CountAsync(ct);

    /// <summary>A source to test or compare with; it never changes the saved source's request spacing.</summary>
    public CustomSource Build(CustomSourceDefinition definition) => factory.CreateDraft(definition);

    /// <summary>
    /// An edit makes the health job check the source again on its next tick. The row itself stays,
    /// so a source that was failing announces its recovery. A deleted source loses its row.
    /// </summary>
    private async Task AfterWriteAsync(string sourceName, CancellationToken ct, bool deleted = false)
    {
        await registry.ReloadAsync(ct);
        listings.EvictSource(sourceName);
        var row = db.HealthChecks.Where(r => r.Id == Jobs.CustomSourceHealthJob.Prefix + sourceName);
        if (deleted)
        {
            await row.ExecuteDeleteAsync(ct);
        }
        else
        {
            var due = DateTime.UtcNow - Jobs.CustomSourceHealthJob.Every - TimeSpan.FromMinutes(1);
            await row.ExecuteUpdateAsync(r => r.SetProperty(x => x.CheckedAt, due), ct);
        }
    }

    private static bool IdentityMayChange(CustomSourceDefinition before, CustomSourceDefinition after) =>
        !SourceLanguages.Same(before.Language, after.Language)
        || !SameHost(before.BaseUrl, after.BaseUrl)
        || before.Engine != after.Engine
        || Json(before.EngineOptions) != Json(after.EngineOptions)
        || Json(before.Series.Url) != Json(after.Series.Url)
        || Json(before.Chapters) != Json(after.Chapters)
        || Json(before.Pages) != Json(after.Pages);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, CustomSourceDefinition.Json);

    private static bool SameHost(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) && CustomHost.Same(x.Host, y.Host);

    /// <summary>
    /// Lists the sample series under both definitions and compares what chapter rows they would
    /// make. A chapter's identity is its number and language, so a change here creates new rows,
    /// wanted and downloaded again, in every linked series.
    /// </summary>
    private async Task<NeedsConfirmation> CompareAsync(CustomSourceDefinition before, CustomSourceDefinition after, CancellationToken ct)
    {
        var reasons = new List<string>();
        if (!SourceLanguages.Same(before.Language, after.Language))
        {
            reasons.Add("language");
        }

        if (!SameHost(before.BaseUrl, after.BaseUrl))
        {
            reasons.Add("host");
        }

        var changes = new List<ChapterChange>();
        try
        {
            var old = Build(before);
            var @new = Build(after);
            var seriesId = old.ResolveSeriesIdFromUrl(new Uri(before.Sample.SeriesUrl))
                ?? throw new InvalidOperationException("sample series unresolved");
            var oldList = await old.ListChaptersAsync(seriesId, ct: ct);
            var newSeriesId = @new.ResolveSeriesIdFromUrl(new Uri(after.Sample.SeriesUrl)) ?? seriesId;
            var newList = await @new.ListChaptersAsync(newSeriesId, ct: ct);

            var oldKeys = oldList.Select(IdentityKey).ToList();
            var newKeys = newList.Select(IdentityKey).ToList();
            if (!oldKeys.SequenceEqual(newKeys))
            {
                reasons.Add("chapters");
                changes.AddRange(oldKeys.Except(newKeys).Take(10).Select(k => new ChapterChange(k, null)));
                changes.AddRange(newKeys.Except(oldKeys).Take(10).Select(k => new ChapterChange(null, k)));
            }

            var oldChapter = SampleChapter(old, before, oldList);
            var newChapter = SampleChapter(@new, after, newList);
            if (oldChapter is not null && newChapter is not null)
            {
                var oldHosts = (await old.GetPagesAsync(oldChapter, ct)).Pages.Select(p => new Uri(p.Url).Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var newHosts = (await @new.GetPagesAsync(newChapter, ct)).Pages.Select(p => new Uri(p.Url).Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!oldHosts.SetEquals(newHosts))
                {
                    reasons.Add("imageHosts");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            reasons.Add("unverified");
        }

        return new NeedsConfirmation(reasons, 0, changes);
    }

    /// <summary>Where a stored entry came from, read off its raw JSON when this build can't parse the whole definition.</summary>
    public static CustomOrigin? OriginOf(StoredCustomSource entry)
    {
        if (entry.Definition is { } definition)
        {
            return definition.Origin;
        }

        try
        {
            return entry.Raw["origin"] is JsonObject origin ? origin.Deserialize<CustomOrigin>(CustomRepository.Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? PreviousOf(StoredCustomSource entry) =>
        entry.Definition?.Previous ?? entry.Raw["previous"]?.DeepClone() as JsonObject;

    /// <summary>
    /// What sync tells chapters apart by (<see cref="ChapterIdentity.Matches"/>: number, volume, and
    /// the label of an unnumbered one), plus the id queued downloads and files hold. A volume
    /// appearing where there was none counts too, which errs on asking.
    /// </summary>
    private static string IdentityKey(SourceChapter chapter)
    {
        var labelled = ChapterIdentity.Labelled(chapter);
        var title = labelled.Number is null ? labelled.Title?.Trim().ToLowerInvariant() : null;
        return $"{labelled.Number}|{labelled.Volume}|{title}|{labelled.SourceChapterId}";
    }

    /// <summary>The listed chapter <see cref="CustomSample.ChapterUrl"/> names, or null.</summary>
    public static SourceChapter? SampleChapter(CustomSource source, CustomSourceDefinition definition, IEnumerable<SourceChapter> chapters)
    {
        if (!Uri.TryCreate(definition.Sample.ChapterUrl, UriKind.Absolute, out var sample))
        {
            return null;
        }

        var list = chapters.ToList();
        return list.FirstOrDefault(c => c.Url is not null && SameUrl(c.Url, sample.AbsoluteUri))
            ?? (CustomTemplate.Match(new Uri(source.BaseUrl), source.ChapterTemplate, sample, CustomTemplate.ChapterId) is { } id
                ? list.FirstOrDefault(c => c.SourceChapterId == id)
                : null);
    }

    public static bool SameUrl(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && CustomHost.Same(x.Host, y.Host)
        && string.Equals(x.AbsolutePath.TrimEnd('/'), y.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
        && x.Query == y.Query;

    public static JsonObject Export(StoredCustomSource entry)
    {
        var json = entry.Raw.DeepClone().AsObject();
        json.Remove("origin");
        json.Remove("previous");
        json.Remove("detached");
        return json;
    }
}
