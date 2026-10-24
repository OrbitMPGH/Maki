using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Http;

namespace Maki.Core.Download;

/// <summary>
/// Minimal qBittorrent WebUI (v2) client: cookie login, add by URL/magnet with a
/// category, and list torrents in that category. One instance per app; the auth
/// cookie is cached and refreshed on 403.
/// </summary>
public class QBittorrentClient : ITorrentClient
{
    private sealed record QbtTorrent(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("progress")] double Progress,
        [property: JsonPropertyName("content_path")] string ContentPath,
        [property: JsonPropertyName("added_on")] long AddedOn,
        [property: JsonPropertyName("category")] string? Category = null);

    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private (string BaseUrl, string Username)? _authenticatedFor;

    public QBittorrentClient()
    {
        // Own client: the auth cookie must persist across requests, which the
        // factory's rotating handlers would discard. That also means the factory's
        // resilience handlers don't apply, so the retry is stacked here by hand.
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var retry = new TransientRetryHandler { InnerHandler = handler };
        Client = new HttpClient(retry) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal QBittorrentClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler);
    }

    private HttpClient Client { get; }

    public TorrentClientKind Kind => TorrentClientKind.QBittorrent;

    public bool UsesUsername => true;

    public string DefaultUrl => "http://localhost:8080";

    public Task PingAsync(TorrentClientSettings settings, CancellationToken ct = default) =>
        GuardAsync<object?>(async () =>
        {
            await EnsureLoginAsync(settings, force: true, ct);
            return null;
        }, ct);

    /// <summary>
    /// Always returns null: qBittorrent fetches a URL later and answers without the hash. A magnet it
    /// already has is refused like a duplicate upload, and handled the same way as in <see cref="AddFileAsync"/>.
    /// </summary>
    public Task<string?> AddAsync(TorrentClientSettings settings, string magnetOrUrl, CancellationToken ct = default) =>
        GuardAsync<string?>(async () =>
        {
            await EnsureLoginAsync(settings, force: false, ct);

            var fields = new Dictionary<string, string>
            {
                ["urls"] = magnetOrUrl,
                ["category"] = settings.Label
            };

            using var response = await SendAuthenticatedAsync(
                settings, HttpMethod.Post, "torrents/add", () => new FormUrlEncodedContent(fields), ct);
            var body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : string.Empty;
            if (response.StatusCode == HttpStatusCode.Conflict || body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
            {
                var infoHash = magnetOrUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                    ? TorrentInfoHash.FromMagnet(magnetOrUrl)
                    : null;
                await AcceptDuplicateAsync(settings, infoHash, ct);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return null;
        }, ct);

    /// <summary>
    /// Uploads a .torrent file. Unlike a URL, which qBittorrent fetches later and so always accepts,
    /// a file it already has is refused; see <see cref="AcceptDuplicateAsync"/>.
    /// </summary>
    public Task AddFileAsync(TorrentClientSettings settings, byte[] torrent, string infoHash, CancellationToken ct = default) =>
        GuardAsync<object?>(async () =>
        {
            await EnsureLoginAsync(settings, force: false, ct);

            using var response = await SendAuthenticatedAsync(settings, HttpMethod.Post, "torrents/add", () =>
            {
                var file = new ByteArrayContent(torrent);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                return new MultipartFormDataContent
                {
                    { file, "torrents", infoHash + ".torrent" },
                    { new StringContent(settings.Label), "category" },
                };
            }, ct);
            var body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : string.Empty;
            if (response.StatusCode == HttpStatusCode.Conflict || body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
            {
                await AcceptDuplicateAsync(settings, infoHash, ct);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return null;
        }, ct);

    /// <summary>
    /// A refused add is fine when qBittorrent already holds the torrent in Maki's category. In another
    /// category the list would never show it, so the grab fails rather than move the user's torrent.
    /// </summary>
    private async Task AcceptDuplicateAsync(TorrentClientSettings settings, string? infoHash, CancellationToken ct)
    {
        var existing = infoHash is null ? null : await FindTorrentAsync(settings, infoHash, ct);
        if (existing is null)
        {
            throw new TorrentClientException(TorrentClientFailure.Rejected, "qBittorrent rejected the torrent");
        }

        if (!string.Equals(existing.Category ?? string.Empty, settings.Label, StringComparison.Ordinal))
        {
            throw new TorrentClientException(TorrentClientFailure.HeldOutsideLabel,
                $"qBittorrent already has this torrent in category '{existing.Category}'");
        }
    }

    private async Task<QbtTorrent?> FindTorrentAsync(TorrentClientSettings settings, string infoHash, CancellationToken ct)
    {
        using var response = await SendAuthenticatedAsync(
            settings, HttpMethod.Get, $"torrents/info?hashes={infoHash}", null, ct);
        response.EnsureSuccessStatusCode();
        var torrents = await response.Content.ReadFromJsonAsync<List<QbtTorrent>>(cancellationToken: ct);
        return torrents?.FirstOrDefault();
    }

    public Task<IReadOnlyList<ClientTorrent>> ListAsync(TorrentClientSettings settings, CancellationToken ct = default) =>
        GuardAsync<IReadOnlyList<ClientTorrent>>(async () =>
        {
            await EnsureLoginAsync(settings, force: false, ct);

            using var response = await SendAuthenticatedAsync(
                settings, HttpMethod.Get, $"torrents/info?category={Uri.EscapeDataString(settings.Label)}", null, ct);
            response.EnsureSuccessStatusCode();
            var torrents = await response.Content.ReadFromJsonAsync<List<QbtTorrent>>(cancellationToken: ct);
            return (torrents ?? []).Select(ToClientTorrent).ToList();
        }, ct);

    private static ClientTorrent ToClientTorrent(QbtTorrent t)
    {
        var errored = t.State is "error" or "missingFiles";
        var checking = t.State.StartsWith("checking", StringComparison.Ordinal);
        return new ClientTorrent(
            t.Hash.ToLowerInvariant(),
            t.Name,
            t.Progress,
            t.Progress >= 1.0 && !errored && !checking,
            t.ContentPath,
            DateTimeOffset.FromUnixTimeSeconds(t.AddedOn),
            errored,
            null);
    }

    /// <summary>A transport failure, or a reply that is not qBittorrent's API, becomes <see cref="TorrentClientFailure.Unreachable"/>.</summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new TorrentClientException(TorrentClientFailure.Unreachable, "qBittorrent is not answering: " + ex.Message, ex);
        }
    }

    /// <summary>Sends a request, logging in again and repeating it once when the session has expired (403).</summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        TorrentClientSettings settings, HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        var response = await SendAsync(settings.Url, method, path, content, ct);
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return response;
        }

        response.Dispose();
        await EnsureLoginAsync(settings, force: true, ct);
        return await SendAsync(settings.Url, method, path, content, ct);
    }

    private async Task EnsureLoginAsync(TorrentClientSettings settings, bool force, CancellationToken ct)
    {
        var session = (settings.Url, settings.Username);
        if (!force && _authenticatedFor == session)
        {
            return;
        }

        await _loginLock.WaitAsync(ct);
        try
        {
            if (!force && _authenticatedFor == session)
            {
                return;
            }

            using var response = await SendAsync(settings.Url, HttpMethod.Post, "auth/login", () => new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["username"] = settings.Username,
                    ["password"] = settings.Password
                }), ct);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                response.EnsureSuccessStatusCode();
            }

            if (!await LoginSucceededAsync(response, ct))
            {
                throw new TorrentClientException(
                    TorrentClientFailure.LoginFailed,
                    "qBittorrent login failed (check username/password). Status code: " + response.StatusCode);
            }

            _authenticatedFor = session;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// qBittorrent 5 answers a good login with 204 and a bad one with 401 (403 once it bans the IP).
    /// 4.x answers both with 200 and tells them apart only by the body, "Ok." or "Fails.".
    /// </summary>
    private static async Task<bool> LoginSucceededAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return true;
        }

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Trim().Equals("Ok.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All requests go through here so they carry Origin/Referer headers matching the
    /// WebUI address: qBittorrent's CSRF protection (on by default) rejects requests
    /// without them, and login then returns 401 even with correct credentials.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        string baseUrl, HttpMethod method, string path,
        Func<HttpContent>? content, CancellationToken ct)
    {
        var webUiRoot = new Uri(baseUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(method, new Uri(webUiRoot, $"api/v2/{path}"));
        request.Headers.Referrer = webUiRoot;
        request.Headers.TryAddWithoutValidation("Origin", webUiRoot.GetLeftPart(UriPartial.Authority));
        request.Content = content?.Invoke();

        return await Client.SendAsync(request, ct);
    }
}
