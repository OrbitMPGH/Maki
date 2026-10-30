using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maki.Core.Inbox;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;

namespace Maki.Api.Services;

/// <summary>
/// Repositories of custom source definitions: adding and removing them, installing from them, and
/// updates, by hand or (opt-in, https only) on their own. Every write of a definition goes through
/// <see cref="CustomSourceService.SaveAsync"/>, so the identity check applies to updates too.
/// </summary>
public sealed class CustomRepositoryService(
    CustomRepositoryStore repositories,
    CustomRepositoryClient client,
    CustomSourceService sources,
    InboxService inbox,
    ILogger<CustomRepositoryService> logger)
{
    /// <summary>A request that can't go ahead, as a catalogue key and its values.</summary>
    public sealed record Refused(string Key, Dictionary<string, string>? Args = null);

    /// <summary>The definition fetched fine but failed its test against the site, so nothing was saved.</summary>
    public sealed record TestFailed(CustomSourceTester.Report Report);

    /// <param name="State">
    /// <c>available</c>, <c>installed</c>, <c>updateAvailable</c>, <c>detached</c> (installed from
    /// here, then edited), <c>idTaken</c> (a source with that id came from elsewhere) or
    /// <c>needsNewerMaki</c>.
    /// </param>
    public sealed record EntryView(string Id, string Name, string Language, int Version, string State, int? InstalledVersion);

    public sealed record RepositoryView(
        string Url, string Name, bool AutoUpdate, bool Secure, DateTime? LastCheckedUtc,
        CustomRepositoryError? LastError, IReadOnlyList<EntryView> Definitions);

    /// <summary>An update waiting for the admin: both versions as stored JSON, for the field-by-field diff.</summary>
    public sealed record UpdatePreview(string Id, int FromVersion, int ToVersion, JsonObject Current, JsonObject Next, IReadOnlyList<string> ChangedFields);

    private const int MaxDefinitionFile = CustomSourceValidator.MaxDefinitionBytes;

    public async Task<IReadOnlyList<RepositoryView>> ListAsync(CancellationToken ct)
    {
        var installed = await sources.ListAsync(ct);
        return (await repositories.ReadAsync(ct)).Select(r => View(r, installed)).ToList();
    }

    public async Task<object> AddAsync(string? url, CancellationToken ct)
    {
        if (CustomRepositoryClient.RepositoryUri(url) is not { } uri)
        {
            return new Refused("error.customRepository.urlInvalid");
        }

        if ((await repositories.ReadAsync(ct)).Any(r => CustomRepositoryStore.SameUrl(r.Url, uri.AbsoluteUri)))
        {
            return new Refused("error.customRepository.exists");
        }

        CustomRepositoryIndex index;
        try
        {
            index = await client.IndexAsync(uri, ct);
        }
        catch (CustomRepositoryClient.FetchFailed ex)
        {
            return new Refused(ex.Key, ex.Args);
        }

        var repository = new CustomRepository
        {
            Url = uri.AbsoluteUri,
            Name = string.IsNullOrWhiteSpace(index.Name) ? uri.Host : index.Name.Trim(),
            Index = index,
            LastCheckedUtc = DateTime.UtcNow
        };
        var added = await repositories.UpdateAsync(list =>
        {
            if (list.Any(r => CustomRepositoryStore.SameUrl(r.Url, repository.Url)))
            {
                return (false, false);
            }

            list.Add(repository);
            return (true, true);
        }, ct);

        return added ? View(repository, await sources.ListAsync(ct)) : new Refused("error.customRepository.exists");
    }

    /// <summary>
    /// Removes the repository. Definitions installed from it keep working as hand-made ones: their
    /// <c>origin</c> goes, so no update can reach them any more.
    /// </summary>
    public async Task<object> RemoveAsync(string url, CancellationToken ct)
    {
        var removed = await repositories.UpdateAsync(list => list.RemoveAll(r => CustomRepositoryStore.SameUrl(r.Url, url)) is var n && n > 0
            ? (true, true)
            : (false, false), ct);
        if (!removed)
        {
            return new Refused("error.customRepository.notFound");
        }

        var detached = 0;
        foreach (var entry in await sources.ListAsync(ct))
        {
            if (entry.Definition is { Origin: { } origin } definition && CustomRepositoryStore.SameUrl(origin.Repository, url))
            {
                await sources.SaveAsync(definition with { Origin = null, Detached = false }, confirmed: true, ct, fromRepository: true, expected: entry.Raw);
                detached++;
            }
        }

        return detached;
    }

    public async Task<object> SetAutoUpdateAsync(string url, bool on, CancellationToken ct) =>
        await repositories.UpdateAsync<object>(list =>
        {
            var index = list.FindIndex(r => CustomRepositoryStore.SameUrl(r.Url, url));
            if (index < 0)
            {
                return (false, new Refused("error.customRepository.notFound"));
            }

            if (on && !list[index].Secure)
            {
                return (false, new Refused("error.customRepository.autoUpdateNeedsHttps"));
            }

            list[index] = list[index] with { AutoUpdate = on };
            return (true, true);
        }, ct);

    /// <summary>Fetches the index again, of one repository or of every one, keeping the old index when the fetch fails.</summary>
    public async Task RefreshAsync(string? url, CancellationToken ct)
    {
        foreach (var repository in await repositories.ReadAsync(ct))
        {
            if (url is not null && !CustomRepositoryStore.SameUrl(repository.Url, url))
            {
                continue;
            }

            CustomRepositoryIndex? index = null;
            CustomRepositoryError? error = null;
            try
            {
                index = await client.IndexAsync(new Uri(repository.Url), ct);
            }
            catch (CustomRepositoryClient.FetchFailed ex)
            {
                error = new CustomRepositoryError(ex.Key, ex.Args);
            }

            await Replace(repository.Url, r => r with
            {
                Index = index ?? r.Index,
                Name = index is { Name.Length: > 0 } ? index.Name.Trim() : r.Name,
                LastCheckedUtc = DateTime.UtcNow,
                LastError = error
            }, ct);
        }
    }

    /// <summary>
    /// Installs one definition, or replaces a detached copy of it. It is tested against its own
    /// sample first and saved only when that passes.
    /// </summary>
    public async Task<object> InstallAsync(string url, string id, bool confirmed, CancellationToken ct)
    {
        if (await FindEntryAsync(url, id, ct) is not { } found)
        {
            return new Refused("error.customRepository.entryNotFound", new() { ["id"] = id });
        }

        var (repository, entry) = found;

        // An entry this build can't read still counts: only one installed from here may be replaced.
        var existing = await sources.FindAsync(entry.Id, ct);
        if (existing is not null
            && (CustomSourceService.OriginOf(existing) is not { } current || !CustomRepositoryStore.SameUrl(current.Repository, repository.Url)))
        {
            return new Refused("error.customRepository.idTaken", new() { ["id"] = entry.Id });
        }

        var fetched = await FetchDefinitionAsync(repository, entry, ct);
        if (fetched is not CustomSourceDefinition next)
        {
            return fetched!;
        }

        var report = await CustomSourceTester.RunAsync(sources.Build(next), ct);
        if (!report.Passed)
        {
            return new TestFailed(report);
        }

        return await sources.SaveAsync(next with { Previous = existing?.Definition?.Previous }, confirmed, ct, fromRepository: true);
    }

    public async Task<object> PreviewUpdateAsync(string id, CancellationToken ct)
    {
        if (await PendingUpdateAsync(id, ct) is not { } pending)
        {
            return new Refused("error.customRepository.noUpdate", new() { ["id"] = id });
        }

        var (repository, entry, stored) = pending;

        var fetched = await FetchDefinitionAsync(repository, entry, ct);
        if (fetched is not CustomSourceDefinition next)
        {
            return fetched!;
        }

        var current = Comparable(stored.Raw);
        var incoming = Comparable(CustomSourceStore.ToJson(next));
        var fields = current.Select(p => p.Key).Union(incoming.Select(p => p.Key))
            .Where(k => !JsonNode.DeepEquals(current[k], incoming[k]))
            .Order(StringComparer.Ordinal)
            .ToList();
        return new UpdatePreview(id, stored.Definition!.Origin!.Version, entry.Version, current, incoming, fields);
    }

    /// <summary>Applies a waiting update by hand. The identity check still asks before chapters could be re-keyed.</summary>
    public async Task<object> ApplyUpdateAsync(string id, bool confirmed, CancellationToken ct)
    {
        if (await PendingUpdateAsync(id, ct) is not { } pending)
        {
            return new Refused("error.customRepository.noUpdate", new() { ["id"] = id });
        }

        return await ApplyAsync(pending.Repository, pending.Entry, pending.Stored, confirmed, ct);
    }

    /// <summary>Puts back the definition the last update replaced.</summary>
    public async Task<object> RevertAsync(string id, bool confirmed, CancellationToken ct)
    {
        var stored = await sources.FindAsync(id, ct);
        if (stored?.Definition?.Previous is not { } previous)
        {
            return new Refused("error.customRepository.nothingToRevert", new() { ["id"] = id });
        }

        CustomSourceDefinition back;
        try
        {
            back = CustomSourceDefinition.Parse(previous.ToJsonString());
        }
        catch (JsonException)
        {
            return new Refused("error.customRepository.nothingToRevert", new() { ["id"] = id });
        }

        // Without this the next daily pass would put the version the admin just left straight back.
        var skip = stored.Definition.Origin?.Version;
        var origin = back.Origin is { } o && skip is not null ? o with { SkipVersion = skip } : back.Origin;
        return await sources.SaveAsync(back with { Previous = null, Detached = false, Origin = origin }, confirmed, ct,
            fromRepository: true, expected: stored.Raw);
    }

    /// <summary>
    /// The daily pass: every index fetched again, and for repositories that keep their sources up
    /// to date, an update applied when its file matches its hash, it validates, its test passes and
    /// the identity check finds nothing. Anything else waits as "Update available", with the reason
    /// on the repository.
    /// </summary>
    public async Task RunDailyAsync(CancellationToken ct)
    {
        await RefreshAsync(null, ct);
        foreach (var repository in await repositories.ReadAsync(ct))
        {
            if (!repository.AutoUpdate || !repository.Secure || repository.Index is null)
            {
                continue;
            }

            foreach (var stored in await sources.ListAsync(ct))
            {
                if (Pending(repository, stored) is not { } entry || entry.Version <= stored.Definition?.Origin?.SkipVersion)
                {
                    continue;
                }

                // An https index can still point at a plain http file, and the checksum is optional.
                // Neither is enough to apply unseen.
                if (!Uri.TryCreate(new Uri(repository.Url), entry.Url, out var file) || file.Scheme != Uri.UriSchemeHttps
                    || string.IsNullOrWhiteSpace(entry.Sha256))
                {
                    await Replace(repository.Url, r => r with
                    {
                        LastError = new CustomRepositoryError("error.customRepository.autoUpdateUnsafe", new() { ["id"] = entry.Id })
                    }, ct);
                    continue;
                }

                var result = await ApplyAsync(repository, entry, stored, confirmed: false, ct, requireTest: true);
                var held = result switch
                {
                    CustomSourceService.Saved => null,
                    Refused refused => new CustomRepositoryError(refused.Key, refused.Args),
                    TestFailed => new CustomRepositoryError("error.customRepository.testFailed", new() { ["id"] = entry.Id }),
                    CustomSourceService.NeedsConfirmation => new CustomRepositoryError("error.customRepository.updateNeedsReview", new() { ["id"] = entry.Id }),
                    _ => new CustomRepositoryError("error.customRepository.updateNeedsReview", new() { ["id"] = entry.Id })
                };

                if (held is not null)
                {
                    logger.LogInformation("Update of custom source {Id} to version {Version} held: {Reason}", entry.Id, entry.Version, held.Key);
                    await Replace(repository.Url, r => r with { LastError = held }, ct);
                    continue;
                }

                var from = stored.Definition!.Origin!.Version;
                logger.LogInformation("Custom source {Id} updated from version {From} to {To} by {Repository}", entry.Id, from, entry.Version, repository.Url);
                inbox.Raise(InboxEventType.CustomSourceUpdated, new InboxMessage(
                        Key: "inbox.customSource.updated",
                        Params: InboxMessage.Args(new { source = stored.Definition.Name, from, to = entry.Version }),
                        Url: CustomSourceSettingsLink(entry.Id)),
                    InboxAudience.Admins);
            }
        }
    }

    public static string CustomSourceSettingsLink(string id) =>
        $"/settings?tab=downloads&s=sources&sources=edit:{CustomSourceDefinition.NamePrefix}{id}";

    private async Task<object> ApplyAsync(
        CustomRepository repository, CustomRepositoryEntry entry, StoredCustomSource stored, bool confirmed, CancellationToken ct,
        bool requireTest = false)
    {
        var fetched = await FetchDefinitionAsync(repository, entry, ct);
        if (fetched is not CustomSourceDefinition next)
        {
            return fetched!;
        }

        if (requireTest && !(await CustomSourceTester.RunAsync(sources.Build(next), ct)).Passed)
        {
            return new TestFailed(new CustomSourceTester.Report(false, []));
        }

        // One step back is kept, never a chain.
        var previous = stored.Raw.DeepClone().AsObject();
        previous.Remove("previous");
        return await sources.SaveAsync(next with { Previous = previous }, confirmed, ct, fromRepository: true, expected: stored.Raw);
    }

    /// <summary>The definition file, checked against the entry: hash, id, schema, and the validator.</summary>
    private async Task<object?> FetchDefinitionAsync(CustomRepository repository, CustomRepositoryEntry entry, CancellationToken ct)
    {
        var args = new Dictionary<string, string> { ["id"] = entry.Id };
        if (entry.Schema != CustomSourceDefinition.CurrentSchema)
        {
            return new Refused("error.customRepository.needsNewerMaki", args);
        }

        if (!Uri.TryCreate(new Uri(repository.Url), entry.Url, out var fileUrl) || CustomRepositoryClient.RepositoryUri(fileUrl.AbsoluteUri) is null)
        {
            return new Refused("error.customRepository.urlInvalid");
        }

        byte[] bytes;
        try
        {
            bytes = await client.GetAsync(fileUrl, MaxDefinitionFile, ct);
        }
        catch (CustomRepositoryClient.FetchFailed ex)
        {
            return new Refused(ex.Key, ex.Args);
        }

        if (entry.Sha256 is { Length: > 0 } expected
            && !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new Refused("error.customRepository.hashMismatch", args);
        }

        CustomSourceDefinition definition;
        try
        {
            definition = CustomSourceDefinition.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (JsonException ex)
        {
            return new Refused("error.customRepository.definitionInvalid", new(args) { ["reason"] = ex.Message });
        }

        if (!string.Equals(definition.Id, entry.Id, StringComparison.Ordinal))
        {
            return new Refused("error.customRepository.idMismatch", args);
        }

        var problems = sources.Validate(definition);
        if (problems.Count > 0)
        {
            return new Refused("error.customRepository.definitionInvalid", new(args) { ["reason"] = string.Join("; ", problems) });
        }

        return definition with
        {
            Origin = new CustomOrigin { Repository = repository.Url, Version = entry.Version, Sha256 = entry.Sha256 },
            Detached = false
        };
    }

    private async Task<(CustomRepository Repository, CustomRepositoryEntry Entry)?> FindEntryAsync(string url, string id, CancellationToken ct)
    {
        var repository = (await repositories.ReadAsync(ct)).FirstOrDefault(r => CustomRepositoryStore.SameUrl(r.Url, url));
        var entry = repository?.Index?.Definitions.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
        return repository is null || entry is null ? null : (repository, entry);
    }

    private async Task<(CustomRepository Repository, CustomRepositoryEntry Entry, StoredCustomSource Stored)?> PendingUpdateAsync(string id, CancellationToken ct)
    {
        var stored = await sources.FindAsync(id, ct);
        if (stored?.Definition?.Origin is not { } origin)
        {
            return null;
        }

        var repository = (await repositories.ReadAsync(ct)).FirstOrDefault(r => CustomRepositoryStore.SameUrl(r.Url, origin.Repository));
        return repository is not null && Pending(repository, stored) is { } entry ? (repository, entry, stored) : null;
    }

    /// <summary>The index entry newer than an installed, not detached definition from this repository.</summary>
    public static CustomRepositoryEntry? Pending(CustomRepository repository, StoredCustomSource stored) =>
        stored.Definition is { Origin: { } origin, Detached: false } definition
        && CustomRepositoryStore.SameUrl(origin.Repository, repository.Url)
        && repository.Index?.Definitions.FirstOrDefault(d => string.Equals(d.Id, definition.Id, StringComparison.Ordinal)) is { } entry
        && entry.Version > origin.Version
            ? entry
            : null;

    private static RepositoryView View(CustomRepository repository, IReadOnlyList<StoredCustomSource> installed)
    {
        var entries = (repository.Index?.Definitions ?? []).Select(entry =>
        {
            var stored = installed.FirstOrDefault(s => string.Equals(s.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            var origin = stored is null ? null : CustomSourceService.OriginOf(stored);
            var fromHere = origin is not null && CustomRepositoryStore.SameUrl(origin.Repository, repository.Url);
            var state = entry.Schema != CustomSourceDefinition.CurrentSchema ? "needsNewerMaki"
                : stored is null ? "available"
                : !fromHere ? "idTaken"
                : stored.Definition is null || stored.Definition.Detached ? "detached"
                : entry.Version > origin!.Version ? "updateAvailable"
                : "installed";
            return new EntryView(entry.Id, entry.Name, entry.Language, entry.Version, state, fromHere ? origin!.Version : null);
        }).ToList();
        return new RepositoryView(repository.Url, repository.Name, repository.AutoUpdate, repository.Secure,
            repository.LastCheckedUtc, repository.LastError, entries);
    }

    /// <summary>A stored entry without the fields that are about where it came from, for the diff.</summary>
    private static JsonObject Comparable(JsonObject raw)
    {
        var copy = raw.DeepClone().AsObject();
        copy.Remove("origin");
        copy.Remove("previous");
        copy.Remove("detached");
        return copy;
    }

    private Task Replace(string url, Func<CustomRepository, CustomRepository> change, CancellationToken ct) =>
        repositories.UpdateAsync(list =>
        {
            var index = list.FindIndex(r => CustomRepositoryStore.SameUrl(r.Url, url));
            if (index < 0)
            {
                return (false, false);
            }

            list[index] = change(list[index]);
            return (true, true);
        }, ct);
}
