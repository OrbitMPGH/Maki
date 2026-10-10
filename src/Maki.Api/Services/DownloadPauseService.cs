using System.Text.Json;
using Maki.Core.Configuration;

namespace Maki.Api.Services;

/// <param name="Until">When the pause lifts by itself, or null for "until somebody resumes".</param>
public sealed record DownloadPauseEntry(DateTime? Until);

/// <param name="All">A pause on every scraper download.</param>
/// <param name="Sources">Paused sources by name; the same names the queue and source settings use.</param>
public sealed record DownloadPauseState(DownloadPauseEntry? All, IReadOnlyDictionary<string, DownloadPauseEntry> Sources)
{
    public static readonly DownloadPauseState None = new(null, new Dictionary<string, DownloadPauseEntry>());

    public bool IsEmpty => All is null && Sources.Count == 0;

    private static bool Lapsed(DownloadPauseEntry entry, DateTime now) => entry.Until is { } until && until <= now;

    public bool HasLapsed(DateTime now) => (All is { } all && Lapsed(all, now)) || Sources.Values.Any(e => Lapsed(e, now));

    /// <summary>This state with every pause whose resume time has passed dropped; the same instance when none has.</summary>
    public DownloadPauseState WithoutLapsed(DateTime now) =>
        !HasLapsed(now)
            ? this
            : new DownloadPauseState(
                All is { } all && !Lapsed(all, now) ? all : null,
                Sources.Where(pair => !Lapsed(pair.Value, now)).ToDictionary(pair => pair.Key, pair => pair.Value));
}

/// <summary>
/// The pause on scraper downloads, persisted in app settings so it survives a restart. A pause only
/// stops <see cref="DownloadQueueService.ClaimNextAsync"/> handing out new items: nothing in flight is
/// cancelled and no row changes status. Reads are served from the settings cache, so the claim path
/// costs no query. A pause whose resume time has passed counts as lifted on the very next read;
/// <see cref="ExpireAsync"/> only tidies the stored copy and tells the caller something changed.
/// </summary>
public class DownloadPauseService(IAppSettings settings, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Parsed(string? Raw, DownloadPauseState State);

    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile Parsed _parsed = new(null, DownloadPauseState.None);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>What is paused right now, with lapsed pauses already left out.</summary>
    public async Task<DownloadPauseState> ActiveAsync(CancellationToken ct = default) =>
        (await LoadAsync(ct)).WithoutLapsed(Now);

    /// <summary>Pauses everything (<paramref name="source"/> null) or one source, replacing any earlier pause of that scope.</summary>
    public async Task<DownloadPauseState> PauseAsync(string? source, DateTime? until, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            var state = (await LoadAsync(ct)).WithoutLapsed(Now);
            var entry = new DownloadPauseEntry(until);
            state = source is null
                ? state with { All = entry }
                : state with { Sources = new Dictionary<string, DownloadPauseEntry>(state.Sources) { [source] = entry } };
            await SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Lifts the global pause (<paramref name="source"/> null) or one source's.</summary>
    public async Task<DownloadPauseState> ResumeAsync(string? source, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            var state = (await LoadAsync(ct)).WithoutLapsed(Now);
            state = source is null
                ? state with { All = null }
                : state with { Sources = state.Sources.Where(pair => pair.Key != source).ToDictionary(pair => pair.Key, pair => pair.Value) };
            await SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>
    /// Removes the pauses whose resume time has passed from the stored copy. True when there was
    /// one, which is the caller's cue to tell open pages and wake the workers.
    /// </summary>
    public async Task<bool> ExpireAsync(CancellationToken ct = default)
    {
        if (!(await LoadAsync(ct)).HasLapsed(Now))
        {
            return false;
        }

        await _write.WaitAsync(ct);
        try
        {
            var stored = await LoadAsync(ct);
            if (!stored.HasLapsed(Now))
            {
                return false;
            }

            await SaveAsync(stored.WithoutLapsed(Now), ct);
            return true;
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task<DownloadPauseState> LoadAsync(CancellationToken ct)
    {
        var raw = await settings.GetAsync(SettingKeys.DownloadPause, ct);
        var cached = _parsed;
        if (cached.Raw == raw)
        {
            return cached.State;
        }

        var state = Parse(raw);
        _parsed = new Parsed(raw, state);
        return state;
    }

    private static DownloadPauseState Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DownloadPauseState.None;
        }

        try
        {
            var state = JsonSerializer.Deserialize<DownloadPauseState>(raw, Json);
            return state is null ? DownloadPauseState.None : state with { Sources = state.Sources ?? DownloadPauseState.None.Sources };
        }
        catch (JsonException)
        {
            return DownloadPauseState.None;
        }
    }

    private async Task SaveAsync(DownloadPauseState state, CancellationToken ct)
    {
        var raw = state.IsEmpty ? null : JsonSerializer.Serialize(state, Json);
        await settings.SetAsync(SettingKeys.DownloadPause, raw, ct);
        _parsed = new Parsed(raw, state);
    }
}
