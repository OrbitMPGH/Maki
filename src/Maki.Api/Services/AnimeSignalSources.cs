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
public class AnimeSignalSources(ScrobbleService scrobbler)
{
    public virtual async Task<IReadOnlyList<IAnimeListSource>> ConnectedAsync(
        int userId, CancellationToken ct = default) =>
        (await scrobbler.ActiveTrackersAsync(userId, ct)).OfType<IAnimeListSource>().ToList();

    public static string NameOf(IAnimeListSource source) => ((IScrobbleTracker)source).Name;
}
