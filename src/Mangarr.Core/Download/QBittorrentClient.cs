using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mangarr.Core.Download;

/// <summary>
/// Minimal qBittorrent WebUI (v2) client: cookie login, add by URL/magnet with a
/// category, and list torrents in that category. One instance per app; the auth
/// cookie is cached and refreshed on 403.
/// </summary>
public class QBittorrentClient
{
    public const string HttpClientName = "qbittorrent";

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
    private string? _authenticatedFor;

    public QBittorrentClient()
    {
        // Own client: the auth cookie must persist across requests, which the
        // factory's rotating handlers would discard.
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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

    public async Task AddAsync(
        string baseUrl, string username, string password,
        string urlOrMagnet, string category, CancellationToken ct = default)
    {
        await EnsureLoginAsync(baseUrl, username, password, force: false, ct);

        var fields = new Dictionary<string, string>
        {
            ["urls"] = urlOrMagnet,
            ["category"] = category
        };

        var response = await SendAsync(baseUrl, HttpMethod.Post, "torrents/add", fields, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            await EnsureLoginAsync(baseUrl, username, password, force: true, ct);
            response = await SendAsync(baseUrl, HttpMethod.Post, "torrents/add", fields, ct);
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("qBittorrent rejected the torrent");
        }
    }

    public async Task<IReadOnlyList<QbtTorrent>> ListAsync(
        string baseUrl, string username, string password, string category, CancellationToken ct = default)
    {
        await EnsureLoginAsync(baseUrl, username, password, force: false, ct);

        var response = await SendAsync(
            baseUrl, HttpMethod.Get, $"torrents/info?category={Uri.EscapeDataString(category)}", null, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            await EnsureLoginAsync(baseUrl, username, password, force: true, ct);
            response = await SendAsync(
                baseUrl, HttpMethod.Get, $"torrents/info?category={Uri.EscapeDataString(category)}", null, ct);
        }

        response.EnsureSuccessStatusCode();
        var torrents = await response.Content.ReadFromJsonAsync<List<QbtTorrent>>(cancellationToken: ct);
        return torrents ?? [];
    }

    private async Task EnsureLoginAsync(string baseUrl, string username, string password, bool force, CancellationToken ct)
    {
        if (!force && _authenticatedFor == baseUrl)
        {
            return;
        }

        await _loginLock.WaitAsync(ct);
        try
        {
            if (!force && _authenticatedFor == baseUrl)
            {
                return;
            }

            var response = await SendAsync(baseUrl, HttpMethod.Post, "auth/login", new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            }, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!body.Contains("Ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("qBittorrent login failed (check username/password)");
            }

            _authenticatedFor = baseUrl;
        }
        finally
        {
            _loginLock.Release();
        }
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
