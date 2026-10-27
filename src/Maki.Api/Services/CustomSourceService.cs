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
        var raw = body.GetRawText();
        if (raw.Length > CustomSourceValidator.MaxDefinitionBytes)
        {
            return (null, new CustomSourceProblem("error.customSource.tooLarge", new { max = CustomSourceValidator.MaxDefinitionBytes / 1024 }));
        }

        try
        {
            return (CustomSourceDefinition.Parse(body), null);
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

    public async Task<object> SaveAsync(CustomSourceDefinition definition, bool confirmed, CancellationToken ct)
    {
        var problems = Validate(definition);
        if (problems.Count > 0)
        {
            return new Refused(problems);
        }

        var existing = await FindAsync(definition.Id, ct);
        if (!confirmed && existing?.Definition is { } before && IdentityMayChange(before, definition))
        {
            var linked = await LinkedSeriesAsync(definition.SourceName, ct);
            if (linked > 0 && await CompareAsync(before, definition, ct) is { Reasons.Count: > 0 } diff)
            {
                return diff with { LinkedSeries = linked };
            }
        }

        var created = await store.UpdateAsync(entries =>
        {
            var json = CustomSourceStore.ToJson(definition);
            var index = entries.FindIndex(e => string.Equals(CustomSourceStore.IdOf(e), definition.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                entries[index] = json;
                return (true, false);
            }

            entries.Add(json);
            return (true, true);
        }, ct);

        await AfterWriteAsync(definition.SourceName, ct);
        return new Saved(definition, created);
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

        await AfterWriteAsync(name, ct);
        return true;
    }

    public Task<int> LinkedSeriesAsync(string sourceName, CancellationToken ct) =>
        db.SourceMappings.Where(m => m.SourceName == sourceName).Select(m => m.SeriesId).Distinct().CountAsync(ct);

    public CustomSource Build(CustomSourceDefinition definition) => factory.Create(definition);

    /// <summary>
    /// Also drops the source's health row: an edit is checked again on the health job's next tick,
    /// and a deleted source has nothing left to report.
    /// </summary>
    private async Task AfterWriteAsync(string sourceName, CancellationToken ct)
    {
        await registry.ReloadAsync(ct);
        listings.EvictSource(sourceName);
        await db.HealthChecks.Where(r => r.Id == Jobs.CustomSourceHealthJob.Prefix + sourceName).ExecuteDeleteAsync(ct);
    }

    private static bool IdentityMayChange(CustomSourceDefinition before, CustomSourceDefinition after) =>
        !SourceLanguages.Same(before.Language, after.Language)
        || !SameHost(before.BaseUrl, after.BaseUrl)
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

            var oldKeys = oldList.Select(c => $"{c.Number}|{c.SourceChapterId}").ToList();
            var newKeys = newList.Select(c => $"{c.Number}|{c.SourceChapterId}").ToList();
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
        return json;
    }
}
