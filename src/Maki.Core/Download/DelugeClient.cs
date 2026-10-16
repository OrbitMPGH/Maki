using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Http;

namespace Maki.Core.Download;

/// <summary>
/// Deluge 2.x through the web UI's JSON-RPC endpoint. The session is the <c>_session_id</c> cookie, the web UI
/// has a password and no username, and it may be logged in without being attached to a daemon, so every
/// fresh login also checks <c>web.connected</c>. Labels need the Label plugin; when it cannot be enabled the
/// driver stops labelling and lists every torrent. Once labels work, failing to label a new torrent fails
/// the grab rather than leaving a torrent the label filter would hide.
/// </summary>
public partial class DelugeClient : ITorrentClient
{
    private const int NotAuthenticated = 1;
    private const string LabelPlugin = "Label";

    private static readonly string[] ListKeys =
    [
        "hash", "name", "progress", "state", "is_finished", "download_location",
        "move_completed", "move_completed_path", "time_added", "message",
    ];

    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly SemaphoreSlim _labelLock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _labelsAvailable = new();
    private (string BaseUrl, string Password)? _session;
    private int _nextId;

    public DelugeClient()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var retry = new TransientRetryHandler { InnerHandler = handler };
        Client = new HttpClient(retry) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal DelugeClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler);
    }

    private HttpClient Client { get; }

    public TorrentClientKind Kind => TorrentClientKind.Deluge;

    public bool UsesUsername => false;

    public string DefaultUrl => "http://localhost:8112";

    public async Task PingAsync(TorrentClientSettings settings, CancellationToken ct = default)
    {
        await EnsureSessionAsync(settings, force: true, ct);
    }

    public async Task<string?> AddAsync(TorrentClientSettings settings, string magnetOrUrl, CancellationToken ct = default)
    {
        var method = magnetOrUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? "core.add_torrent_magnet"
            : "core.add_torrent_url";

        string? hash;
        try
        {
            var result = await CallAsync(settings, method, ct, magnetOrUrl, new Dictionary<string, object>());
            hash = result.ValueKind == JsonValueKind.String ? result.GetString() : null;
        }
        catch (DelugeRpcException ex) when (AlreadyPresent(ex, out var present))
        {
            hash = present;
        }
        catch (DelugeRpcException ex)
        {
            throw Rejected(ex);
        }

        hash = string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().ToLowerInvariant();
        if (hash is not null)
        {
            await LabelAsync(settings, hash, ct);
        }

        return hash;
    }

    public async Task AddFileAsync(TorrentClientSettings settings, byte[] torrent, string infoHash, CancellationToken ct = default)
    {
        try
        {
            await CallAsync(
                settings, "core.add_torrent_file", ct,
                infoHash + ".torrent", Convert.ToBase64String(torrent), new Dictionary<string, object>());
        }
        catch (DelugeRpcException ex) when (AlreadyPresent(ex, out _))
        {
        }
        catch (DelugeRpcException ex)
        {
            throw Rejected(ex);
        }

        await LabelAsync(settings, infoHash.ToLowerInvariant(), ct);
    }

    public async Task<IReadOnlyList<ClientTorrent>> ListAsync(TorrentClientSettings settings, CancellationToken ct = default)
    {
        var filter = new Dictionary<string, object>();
        if (await LabelsAvailableAsync(settings, ct) is { } label)
        {
            filter["label"] = label;
        }

        JsonElement result;
        try
        {
            result = await CallAsync(settings, "web.update_ui", ct, ListKeys, filter);
            if (!TryGetTorrents(result, out _))
            {
                await EnsureConnectedAsync(settings, ct);
                result = await CallAsync(settings, "web.update_ui", ct, ListKeys, filter);
            }
        }
        catch (DelugeRpcException ex)
        {
            throw new TorrentClientException(TorrentClientFailure.Unreachable, "Deluge could not list torrents: " + ex.Message, ex);
        }

        if (!TryGetTorrents(result, out var torrents))
        {
            throw new TorrentClientException(TorrentClientFailure.Unreachable, "Deluge web UI is not connected to a daemon");
        }

        var list = new List<ClientTorrent>();
        foreach (var entry in torrents.EnumerateObject())
        {
            list.Add(ToClientTorrent(entry.Name, entry.Value));
        }

        return list;
    }

    private static bool TryGetTorrents(JsonElement result, out JsonElement torrents)
    {
        torrents = default;
        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("torrents", out torrents)
            && torrents.ValueKind == JsonValueKind.Object;
    }

    private static ClientTorrent ToClientTorrent(string key, JsonElement t)
    {
        var hash = (String(t, "hash") ?? key).ToLowerInvariant();
        var state = String(t, "state") ?? string.Empty;
        var progress = Number(t, "progress");
        var finished = Bool(t, "is_finished");
        var errored = state.Equals("Error", StringComparison.OrdinalIgnoreCase);
        var settling = errored
            || state.Equals("Checking", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Allocating", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Moving", StringComparison.OrdinalIgnoreCase);

        var message = String(t, "message");
        var addedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(Number(t, "time_added") * 1000));
        return new ClientTorrent(
            hash,
            String(t, "name") ?? string.Empty,
            progress / 100.0,
            (finished || progress >= 100) && !settling,
            ContentPath(t, finished),
            addedAt,
            errored,
            string.IsNullOrWhiteSpace(message) ? null : message);
    }

    private static string ContentPath(JsonElement t, bool finished)
    {
        var name = String(t, "name") ?? string.Empty;
        var moved = String(t, "move_completed_path");
        var folder = finished && Bool(t, "move_completed") && !string.IsNullOrEmpty(moved)
            ? moved
            : String(t, "download_location") ?? string.Empty;
        if (folder.Length == 0)
        {
            return name;
        }

        var separator = folder.Contains('\\') && !folder.Contains('/') ? '\\' : '/';
        return folder.TrimEnd('/', '\\') + separator + name;
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static TorrentClientException Rejected(DelugeRpcException ex) =>
        new(TorrentClientFailure.Rejected, "Deluge rejected the torrent: " + ex.Message, ex);

    [GeneratedRegex(@"already in session[^0-9a-fA-F]*([0-9a-fA-F]{40})?", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyInSession();

    /// <summary>Deluge raises, rather than answering null, when a magnet or file is already in the session.</summary>
    private static bool AlreadyPresent(DelugeRpcException ex, out string? hash)
    {
        var match = AlreadyInSession().Match(ex.Message);
        hash = match.Success && match.Groups[1].Success ? match.Groups[1].Value : null;
        return match.Success;
    }

    /// <summary>Whether Deluge can store the label: it lowercases it and allows only letters, digits, dot, dash and underscore.</summary>
    public static bool IsValidLabel(string label) => NormalizeLabel(label) is not null;

    private static string? NormalizeLabel(string label)
    {
        var id = label.Trim().ToLowerInvariant();
        return id.Length > 0 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.')
            ? id
            : null;
    }

    private static string KeyOf(TorrentClientSettings settings) => settings.Url.TrimEnd('/');

    /// <summary>The lowercased label to use, or null when this Deluge cannot label.</summary>
    private async Task<string?> LabelsAvailableAsync(TorrentClientSettings settings, CancellationToken ct)
    {
        var label = NormalizeLabel(settings.Label);
        if (label is null)
        {
            return null;
        }

        var key = KeyOf(settings);
        if (_labelsAvailable.TryGetValue(key, out var known))
        {
            return known ? label : null;
        }

        try
        {
            var plugins = await CallAsync(settings, "core.get_enabled_plugins", ct);
            var enabled = plugins.ValueKind == JsonValueKind.Array
                && plugins.EnumerateArray().Any(p =>
                    p.ValueKind == JsonValueKind.String && p.GetString()!.Equals(LabelPlugin, StringComparison.OrdinalIgnoreCase));
            if (!enabled)
            {
                await CallAsync(settings, "core.enable_plugin", ct, LabelPlugin);
            }

            await CallAsync(settings, "label.get_labels", ct);
        }
        catch (DelugeRpcException)
        {
            _labelsAvailable[key] = false;
            return null;
        }

        _labelsAvailable[key] = true;
        return label;
    }

    /// <summary>
    /// Labels a torrent Deluge now has. A failure is the grab's failure: listing filters on the label, so an
    /// unlabelled torrent would never be seen again. Grabbing again finds the torrent present and retries this.
    /// </summary>
    private async Task LabelAsync(TorrentClientSettings settings, string hash, CancellationToken ct)
    {
        if (await LabelsAvailableAsync(settings, ct) is not { } label)
        {
            return;
        }

        try
        {
            await EnsureLabelExistsAsync(settings, label, ct);
            await CallAsync(settings, "label.set_torrent", ct, hash, label);
        }
        catch (DelugeRpcException ex)
        {
            // The plugin may have been switched off since it was checked; look again next time.
            _labelsAvailable.TryRemove(KeyOf(settings), out _);
            throw new TorrentClientException(
                TorrentClientFailure.Rejected, "Deluge added the torrent but would not label it: " + ex.Message, ex);
        }
    }

    /// <summary>Two grabs at once may both find the label missing; the one that loses the race to create it is fine.</summary>
    private async Task EnsureLabelExistsAsync(TorrentClientSettings settings, string label, CancellationToken ct)
    {
        await _labelLock.WaitAsync(ct);
        try
        {
            if (await HasLabelAsync(settings, label, ct))
            {
                return;
            }

            try
            {
                await CallAsync(settings, "label.add", ct, label);
            }
            catch (DelugeRpcException)
            {
                if (!await HasLabelAsync(settings, label, ct))
                {
                    throw;
                }
            }
        }
        finally
        {
            _labelLock.Release();
        }
    }

    private async Task<bool> HasLabelAsync(TorrentClientSettings settings, string label, CancellationToken ct)
    {
        var labels = await CallAsync(settings, "label.get_labels", ct);
        return labels.ValueKind == JsonValueKind.Array
            && labels.EnumerateArray().Any(l => l.ValueKind == JsonValueKind.String && l.GetString() == label);
    }

    /// <summary>Sends a call, logging in again and repeating it once when the session has expired (error code 1).</summary>
    private async Task<JsonElement> CallAsync(TorrentClientSettings settings, string method, CancellationToken ct, params object?[] args)
    {
        await EnsureSessionAsync(settings, force: false, ct);
        try
        {
            return await SendAsync(settings, method, args, ct);
        }
        catch (DelugeRpcException ex) when (ex.Code == NotAuthenticated)
        {
            await EnsureSessionAsync(settings, force: true, ct);
            try
            {
                return await SendAsync(settings, method, args, ct);
            }
            catch (DelugeRpcException again) when (again.Code == NotAuthenticated)
            {
                throw new TorrentClientException(TorrentClientFailure.LoginFailed, "Deluge kept answering not authenticated", again);
            }
        }
    }

    private async Task EnsureSessionAsync(TorrentClientSettings settings, bool force, CancellationToken ct)
    {
        var session = (KeyOf(settings), settings.Password);
        if (!force && _session == session)
        {
            return;
        }

        await _loginLock.WaitAsync(ct);
        try
        {
            if (!force && _session == session)
            {
                return;
            }

            _session = null;
            try
            {
                var login = await SendAsync(settings, "auth.login", [settings.Password], ct);
                if (login.ValueKind != JsonValueKind.True)
                {
                    throw new TorrentClientException(TorrentClientFailure.LoginFailed, "Deluge login failed (check the password)");
                }

                await ConnectIfNeededAsync(settings, ct);
            }
            catch (DelugeRpcException ex) when (ex.Code == NotAuthenticated)
            {
                throw new TorrentClientException(TorrentClientFailure.LoginFailed, "Deluge login failed: " + ex.Message, ex);
            }
            catch (DelugeRpcException ex)
            {
                throw new TorrentClientException(TorrentClientFailure.Unreachable, "Deluge answered with an error: " + ex.Message, ex);
            }

            _session = session;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(TorrentClientSettings settings, CancellationToken ct)
    {
        await EnsureSessionAsync(settings, force: false, ct);
        try
        {
            await ConnectIfNeededAsync(settings, ct);
        }
        catch (DelugeRpcException ex)
        {
            throw new TorrentClientException(TorrentClientFailure.Unreachable, "Deluge could not connect to a daemon: " + ex.Message, ex);
        }
    }

    private async Task ConnectIfNeededAsync(TorrentClientSettings settings, CancellationToken ct)
    {
        var connected = await SendAsync(settings, "web.connected", [], ct);
        if (connected.ValueKind == JsonValueKind.True)
        {
            return;
        }

        var hosts = await SendAsync(settings, "web.get_hosts", [], ct);
        var hostId = PickHost(hosts)
            ?? throw new TorrentClientException(TorrentClientFailure.Unreachable, "Deluge web UI has no daemon to connect to");
        await SendAsync(settings, "web.connect", [hostId], ct);
    }

    private static string? PickHost(JsonElement hosts)
    {
        if (hosts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? first = null;
        foreach (var row in hosts.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() == 0 || row[0].ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = row[0].GetString()!;
            first ??= id;
            if (row.GetArrayLength() > 1 && row[1].ValueKind == JsonValueKind.String
                && row[1].GetString() is "127.0.0.1" or "localhost")
            {
                return id;
            }
        }

        return first;
    }

    private async Task<JsonElement> SendAsync(TorrentClientSettings settings, string method, object?[] args, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["method"] = method,
            ["params"] = args,
            ["id"] = Interlocked.Increment(ref _nextId),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyOf(settings) + "/json")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        try
        {
            using var response = await Client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new TorrentClientException(
                    TorrentClientFailure.Unreachable, "Deluge web UI answered HTTP " + (int)response.StatusCode);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TorrentClientException(TorrentClientFailure.Unreachable, "Deluge web UI did not answer with JSON-RPC");
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()!
                    : "unknown error";
                throw new DelugeRpcException(code, message);
            }

            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new TorrentClientException(TorrentClientFailure.Unreachable, "Deluge web UI is unreachable: " + ex.Message, ex);
        }
    }

    private sealed class DelugeRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
