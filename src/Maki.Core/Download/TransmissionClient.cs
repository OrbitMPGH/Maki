using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Maki.Core.Http;

namespace Maki.Core.Download;

/// <summary>
/// Transmission RPC driver. Requests carry the X-Transmission-Session-Id header, which the daemon
/// hands out in a 409 and which is cached per (url, username). Transmission 4.1 renamed its fields
/// to snake_case but answers a request without a JSON-RPC 2.0 envelope in the legacy names, so the
/// driver speaks the legacy names on every version and reads the rpc version only to place labels.
/// Labels arrived in rpc 16 (Transmission 3.00); older daemons get no label and list unfiltered.
/// </summary>
public class TransmissionClient : ITorrentClient
{
    private const string SessionHeader = "X-Transmission-Session-Id";
    private const int LabelsVersion = 16;
    private const int LabelsOnAddVersion = 17;

    private sealed class Session
    {
        public volatile string? SessionId;
        public volatile int RpcVersion;
        public volatile bool Detected;
        public SemaphoreSlim DetectLock { get; } = new(1, 1);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private readonly ConcurrentDictionary<(string Url, string Username), Session> _sessions = new();

    public TransmissionClient()
    {
        var retry = new TransientRetryHandler { InnerHandler = new HttpClientHandler() };
        Client = new HttpClient(retry) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal TransmissionClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler);
    }

    private HttpClient Client { get; }

    public TorrentClientKind Kind => TorrentClientKind.Transmission;

    public bool UsesUsername => true;

    public string DefaultUrl => "http://localhost:9091";

    public async Task PingAsync(TorrentClientSettings settings, CancellationToken ct = default)
    {
        var session = SessionFor(settings);
        await DetectAsync(settings, session, force: true, ct);
    }

    public async Task<string?> AddAsync(TorrentClientSettings settings, string magnetOrUrl, CancellationToken ct = default)
    {
        var session = await EnsureAsync(settings, ct);
        return await AddCoreAsync(settings, session, new Dictionary<string, object?> { ["filename"] = magnetOrUrl }, ct);
    }

    public async Task AddFileAsync(TorrentClientSettings settings, byte[] torrent, string infoHash, CancellationToken ct = default)
    {
        var session = await EnsureAsync(settings, ct);
        await AddCoreAsync(settings, session, new Dictionary<string, object?> { ["metainfo"] = Convert.ToBase64String(torrent) }, ct);
    }

    public async Task<IReadOnlyList<ClientTorrent>> ListAsync(TorrentClientSettings settings, CancellationToken ct = default)
    {
        var session = await EnsureAsync(settings, ct);
        var args = new Dictionary<string, object?>
        {
            ["fields"] = new[]
            {
                "hashString", "name", "percentDone", "isFinished", "status", "error",
                "errorString", "downloadDir", "labels", "addedDate",
            },
        };
        var result = await CallAsync(settings, session, "torrent-get", args, TorrentClientFailure.Unreachable, ct);
        var list = new List<ClientTorrent>();
        if (!result.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var t in torrents.EnumerateArray())
        {
            if (UsesLabels(settings, session) && !HasLabel(t, "labels", settings.Label))
            {
                continue;
            }

            var hash = ReadString(t, "hashString");
            if (hash.Length == 0)
            {
                continue;
            }

            var percent = ReadDouble(t, "percentDone");
            var error = ReadInt(t, "error");
            var status = ReadInt(t, "status");
            var finished = ReadBool(t, "isFinished");
            var complete = (finished || percent >= 1.0) && error != 3 && status is not (1 or 2);
            var name = ReadString(t, "name");
            var added = ReadLong(t, "addedDate");
            var message = ReadString(t, "errorString");

            list.Add(new ClientTorrent(
                hash.ToLowerInvariant(),
                name,
                percent,
                complete,
                JoinPath(ReadString(t, "downloadDir"), name),
                added > 0 ? DateTimeOffset.FromUnixTimeSeconds(added) : DateTimeOffset.MinValue,
                error == 3,
                error != 0 && message.Length > 0 ? message : null));
        }

        return list;
    }

    private async Task<string?> AddCoreAsync(
        TorrentClientSettings settings, Session session, Dictionary<string, object?> args, CancellationToken ct)
    {
        var labelOnAdd = UsesLabels(settings, session) && session.RpcVersion >= LabelsOnAddVersion;
        if (labelOnAdd)
        {
            args["labels"] = new[] { settings.Label };
        }

        var result = await CallAsync(settings, session, "torrent-add", args, TorrentClientFailure.Rejected, ct);
        var duplicate = false;
        if (!result.TryGetProperty("torrent-added", out var entry) || entry.ValueKind != JsonValueKind.Object)
        {
            duplicate = true;
            if (!result.TryGetProperty("torrent-duplicate", out entry) || entry.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
        }

        var hash = ReadString(entry, "hashString");
        if (UsesLabels(settings, session) && entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
        {
            var torrentId = id.GetInt32();
            if (duplicate)
            {
                await EnsureLabelledAsync(settings, session, torrentId, ct);
            }
            else if (!labelOnAdd)
            {
                await SetLabelsAsync(settings, session, torrentId, [settings.Label], ct);
            }
        }

        return hash.Length == 0 ? null : hash.ToLowerInvariant();
    }

    /// <summary>A torrent Transmission already had keeps the user's labels; ours is added when missing.</summary>
    private async Task EnsureLabelledAsync(
        TorrentClientSettings settings, Session session, int torrentId, CancellationToken ct)
    {
        var args = new Dictionary<string, object?> { ["ids"] = new[] { torrentId }, ["fields"] = new[] { "labels" } };
        var result = await CallAsync(settings, session, "torrent-get", args, TorrentClientFailure.Unreachable, ct);
        var labels = new List<string>();
        if (result.TryGetProperty("torrents", out var torrents) && torrents.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in torrents.EnumerateArray())
            {
                if (HasLabel(t, "labels", settings.Label))
                {
                    return;
                }

                if (t.TryGetProperty("labels", out var existing) && existing.ValueKind == JsonValueKind.Array)
                {
                    labels.AddRange(existing.EnumerateArray()
                        .Where(l => l.ValueKind == JsonValueKind.String)
                        .Select(l => l.GetString()!));
                }
            }
        }

        labels.Add(settings.Label);
        await SetLabelsAsync(settings, session, torrentId, labels.ToArray(), ct);
    }

    private async Task SetLabelsAsync(
        TorrentClientSettings settings, Session session, int torrentId, string[] labels, CancellationToken ct)
    {
        var args = new Dictionary<string, object?> { ["ids"] = new[] { torrentId }, ["labels"] = labels };
        await CallAsync(settings, session, "torrent-set", args, TorrentClientFailure.Rejected, ct);
    }

    private static bool UsesLabels(TorrentClientSettings settings, Session session) =>
        settings.Label.Length > 0 && session.RpcVersion >= LabelsVersion;

    private Session SessionFor(TorrentClientSettings settings) =>
        _sessions.GetOrAdd((settings.Url.Trim().TrimEnd('/'), settings.Username), _ => new Session());

    private async Task<Session> EnsureAsync(TorrentClientSettings settings, CancellationToken ct)
    {
        var session = SessionFor(settings);
        if (!session.Detected)
        {
            try
            {
                await DetectAsync(settings, session, force: false, ct);
            }
            catch (TorrentClientException ex) when (ex.Failure == TorrentClientFailure.Unreachable)
            {
                // The real call reports an unreachable daemon.
            }
        }

        return session;
    }

    private async Task DetectAsync(TorrentClientSettings settings, Session session, bool force, CancellationToken ct)
    {
        await session.DetectLock.WaitAsync(ct);
        try
        {
            if (!force && session.Detected)
            {
                return;
            }

            var args = new Dictionary<string, object?>
            {
                ["fields"] = new[] { "rpc-version", "rpc-version-semver", "version", "rpc_version", "rpc_version_semver" },
            };
            var result = await CallAsync(settings, session, "session-get", args, TorrentClientFailure.Unreachable, ct);
            var version = Math.Max(ReadInt(result, "rpc-version"), ReadInt(result, "rpc_version"));
            session.RpcVersion = version;
            session.Detected = true;
        }
        finally
        {
            session.DetectLock.Release();
        }
    }

    private async Task<JsonElement> CallAsync(
        TorrentClientSettings settings, Session session, string method, Dictionary<string, object?> arguments,
        TorrentClientFailure resultFailure, CancellationToken ct)
    {
        if (!Uri.TryCreate(Endpoint(settings.Url), UriKind.Absolute, out var endpoint))
        {
            throw new TorrentClientException(TorrentClientFailure.Unreachable, "Transmission URL is not valid");
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["method"] = method, ["arguments"] = arguments });

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (session.SessionId is { } id)
            {
                request.Headers.TryAddWithoutValidation(SessionHeader, id);
            }

            if (settings.Username.Length > 0)
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new TorrentClientException(TorrentClientFailure.Unreachable, "Transmission did not answer: " + ex.Message, ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    if (attempt == 0 && response.Headers.TryGetValues(SessionHeader, out var values))
                    {
                        session.SessionId = values.FirstOrDefault();
                        continue;
                    }

                    throw new TorrentClientException(TorrentClientFailure.Unreachable, "Transmission refused the session id");
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new TorrentClientException(TorrentClientFailure.LoginFailed, "Transmission login failed (check username/password)");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TorrentClientException(
                        TorrentClientFailure.Unreachable, "Transmission answered with status " + (int)response.StatusCode);
                }

                var text = await response.Content.ReadAsStringAsync(ct);
                return ParseEnvelope(text, resultFailure);
            }
        }
    }

    private static JsonElement ParseEnvelope(string text, TorrentClientFailure resultFailure)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new TorrentClientException(TorrentClientFailure.Unreachable, "Transmission answered with something other than JSON", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TorrentClientException(TorrentClientFailure.Unreachable, "Transmission answered with an unexpected body");
            }

            if (root.TryGetProperty("result", out var result))
            {
                if (result.ValueKind == JsonValueKind.String)
                {
                    var message = result.GetString();
                    if (!string.Equals(message, "success", StringComparison.Ordinal))
                    {
                        throw new TorrentClientException(resultFailure, "Transmission reported: " + message);
                    }

                    return root.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                        ? args.Clone()
                        : EmptyObject;
                }

                if (result.ValueKind == JsonValueKind.Object)
                {
                    return result.Clone();
                }
            }

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : error.ToString();
                throw new TorrentClientException(resultFailure, "Transmission reported: " + message);
            }

            throw new TorrentClientException(TorrentClientFailure.Unreachable, "Transmission answered with an unexpected body");
        }
    }

    private static string Endpoint(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        return trimmed.EndsWith("/rpc", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/transmission/rpc";
    }

    private static string JoinPath(string directory, string name)
    {
        if (directory.Length == 0)
        {
            return name;
        }

        var separator = directory.Contains('\\') && !directory.Contains('/') ? '\\' : '/';
        return directory.TrimEnd('/', '\\') + separator + name;
    }

    private static bool HasLabel(JsonElement torrent, string property, string label) =>
        torrent.TryGetProperty(property, out var labels)
        && labels.ValueKind == JsonValueKind.Array
        && labels.EnumerateArray().Any(l =>
            l.ValueKind == JsonValueKind.String && string.Equals(l.GetString(), label, StringComparison.OrdinalIgnoreCase));

    private static string ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double ReadDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static long ReadLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static int ReadInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool ReadBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
