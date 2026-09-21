using Maki.Core.Configuration;
using Maki.Core.Scrobbling;

namespace Maki.Api.Services;

/// <summary>
/// Which of a user's connected trackers can hand over an anime list.
/// <para>
/// One line over <see cref="ScrobbleService"/>, given its own type because three callers ask the
/// same question and two of them are controllers: taking the whole scrobbler into a controller
/// drags nine constructor parameters into every test that builds one, for a boolean.
/// </para>
/// </summary>
public class AnimeSignalSources(ScrobbleService scrobbler, IUserSettingsStore userSettings)
{
    public virtual async Task<IReadOnlyList<IAnimeListSource>> ConnectedAsync(
        int userId, CancellationToken ct = default) =>
        (await scrobbler.ActiveTrackersAsync(userId, ct)).OfType<IAnimeListSource>().ToList();

    /// <summary>
    /// The subset of <see cref="ConnectedAsync"/> the reader lets feed their taste. Separate from
    /// the capability question on purpose: a tracker they switched off is still connected, so the
    /// panel keeps saying "your trackers" rather than "connect one".
    /// </summary>
    public virtual async Task<IReadOnlyList<IAnimeListSource>> EnabledAsync(
        int userId, CancellationToken ct = default)
    {
        var sources = await ConnectedAsync(userId, ct);
        var allowed = new List<IAnimeListSource>();
        foreach (var source in sources)
        {
            if (await EnabledForAsync(userId, NameOf(source), ct))
            {
                allowed.Add(source);
            }
        }

        return allowed;
    }

    /// <summary>Whether one named tracker's anime list may be read. Unset = on.</summary>
    public virtual async Task<bool> EnabledForAsync(
        int userId, string service, CancellationToken ct = default) =>
        await userSettings.GetAsync(
            userId, SettingKeys.RecommendationsAnimeSignalsSourceKey(service), ct) != "false";

    public static string NameOf(IAnimeListSource source) => ((IScrobbleTracker)source).Name;
}
