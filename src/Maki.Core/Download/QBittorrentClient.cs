using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Maki.Core.Http;

namespace Maki.Core.Download;

public enum QBittorrentFailure
{
    LoginFailed,
    TorrentRejected,
}

/// <summary>A qBittorrent refusal Maki can name; the message is English for logs only.</summary>
public sealed class QBittorrentException(QBittorrentFailure failure, string message) : InvalidOperationException(message)
{
    public QBittorrentFailure Failure { get; } = failure;
}

/// <summary>
/// Minimal qBittorrent WebUI (v2) client: cookie login, add by URL/magnet with a
/// category, and list torrents in that category. One instance per app; the auth
/// cookie is cached and refreshed on 403.
/// </summary>
public class QBittorrentClient
{
    public record QbtTorrent(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("progress")] double Progress,
        [property: JsonPropertyName("content_path")] string ContentPath,
        [property: JsonPropertyName("save_path")] string SavePath,
        [property: JsonPropertyName("added_on")] long AddedOn)
    {
        /// <summary>Downloaded and verified (seeding or paused-complete states).</summary>
        public bool IsComplete => Progress >= 1.0;
    }

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

    public async Task<bool> PingAsync(string baseUrl, string username, string password, CancellationToken ct = default)
    {
        try
        {
            await EnsureLoginAsync(baseUrl, username, password, force: true, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public virtual async Task AddAsync(
        string baseUrl, string username, string password,
        string urlOrMagnet, string category, CancellationToken ct = default)
    {
        await EnsureLoginAsync(baseUrl, username, password, force: false, ct);

        var fields = new Dictionary<string, string>
        {
            ["urls"] = urlOrMagnet,
            ["category"] = category
        };

        using var response = await SendAuthenticatedAsync(
            baseUrl, username, password, HttpMethod.Post, "torrents/add", fields, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
        {
            throw new QBittorrentException(QBittorrentFailure.TorrentRejected, "qBittorrent rejected the torrent");
        }
    }

    public virtual async Task<IReadOnlyList<QbtTorrent>> ListAsync(
        string baseUrl, string username, string password, string category, CancellationToken ct = default)
    {
        await EnsureLoginAsync(baseUrl, username, password, force: false, ct);

        using var response = await SendAuthenticatedAsync(
            baseUrl, username, password, HttpMethod.Get,
            $"torrents/info?category={Uri.EscapeDataString(category)}", null, ct);
        response.EnsureSuccessStatusCode();
        var torrents = await response.Content.ReadFromJsonAsync<List<QbtTorrent>>(cancellationToken: ct);
        return torrents ?? [];
    }

    /// <summary>Sends a request, logging in again and repeating it once when the session has expired (403).</summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        string baseUrl, string username, string password, HttpMethod method, string path,
        IReadOnlyDictionary<string, string>? formFields, CancellationToken ct)
    {
        var response = await SendAsync(baseUrl, method, path, formFields, ct);
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return response;
        }

        response.Dispose();
        await EnsureLoginAsync(baseUrl, username, password, force: true, ct);
        return await SendAsync(baseUrl, method, path, formFields, ct);
    }

    private async Task EnsureLoginAsync(string baseUrl, string username, string password, bool force, CancellationToken ct)
    {
        var session = (baseUrl, username);
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

            using var response = await SendAsync(baseUrl, HttpMethod.Post, "auth/login", new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            }, ct);
            response.EnsureSuccessStatusCode();
            if (!await LoginSucceededAsync(response, ct))
            {
                throw new QBittorrentException(
                    QBittorrentFailure.LoginFailed,
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
    /// qBittorrent 5 answers a good login with 204 and a bad one with 401. 4.x answers both with 200
    /// and tells them apart only by the body, "Ok." or "Fails.".
    /// </summary>
    private static async Task<bool> LoginSucceededAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Trim().Equals("Ok.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All requests go through here so they carry Origin/Referer headers matching the
    /// WebUI address: qBittorrent's CSRF protection (on by default) rejects requests
    /// without them — login returns 401 even with correct credentials.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        string baseUrl, HttpMethod method, string path,
        IReadOnlyDictionary<string, string>? formFields, CancellationToken ct)
    {
        var webUiRoot = new Uri(baseUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(method, new Uri(webUiRoot, $"api/v2/{path}"));
        request.Headers.Referrer = webUiRoot;
        request.Headers.TryAddWithoutValidation("Origin", webUiRoot.GetLeftPart(UriPartial.Authority));
        if (formFields is not null)
        {
            request.Content = new FormUrlEncodedContent(formFields);
        }

        return await Client.SendAsync(request, ct);
    }
}
