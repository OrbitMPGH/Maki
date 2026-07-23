namespace Maki.Core.Scrobbling;

/// <summary>
/// Tracker endpoint URLs. Defaults are the real services; overridable (via the
/// MAKI_SCROBBLE_* env vars in Program.cs) so E2E tests can point at mocks.
/// </summary>
public record ScrobbleTrackerOptions(
    string AniListApiUrl = "https://graphql.anilist.co",
    string AniListOAuthUrl = "https://anilist.co/api/v2/oauth",
    string MalApiUrl = "https://api.myanimelist.net/v2",
    string MalOAuthUrl = "https://myanimelist.net/v1/oauth2",
    string MangaBakaApiUrl = "https://api.mangabaka.org",
    string KitsuApiUrl = "https://kitsu.io/api/edge",
    string KitsuOAuthUrl = "https://kitsu.io/api/oauth");
