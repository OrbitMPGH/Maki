using System.Net;
using System.Text.Json.Nodes;
using Maki.Core.Download;

namespace Maki.Core.Tests;

public class TransmissionClientTests
{
    private static readonly TorrentClientSettings Settings =
        new(TorrentClientKind.Transmission, "http://tr.test:9091", "user", "pw", "maki");

    private sealed record Recorded(string Method, JsonObject Arguments, string? SessionId, string? Authorization);

    private sealed class RpcHandler(int rpcVersion, Func<string, JsonObject, JsonObject?>? respond = null) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];
        public List<string> Paths { get; } = [];
        public int Conflicts { get; set; }
        public string IssuedSessionId { get; set; } = "abc123";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            request.Headers.TryGetValues("X-Transmission-Session-Id", out var ids);
            var sessionId = ids?.FirstOrDefault();
            var root = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var method = root["method"]!.GetValue<string>();
            Requests.Add(new Recorded(method, root["arguments"]!.AsObject(), sessionId, request.Headers.Authorization?.ToString()));

            if (sessionId != IssuedSessionId)
            {
                Conflicts++;
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("conflict") };
                conflict.Headers.Add("X-Transmission-Session-Id", IssuedSessionId);
                return conflict;
            }

            var args = method == "session-get"
                ? new JsonObject { ["rpc-version"] = rpcVersion, ["version"] = "test" }
                : respond?.Invoke(method, root["arguments"]!.AsObject()) ?? new JsonObject();
            var body = new JsonObject { ["result"] = "success", ["arguments"] = args };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        }
    }

    private static JsonObject Added(string key, string hash, int id = 7) =>
        new() { [key] = new JsonObject { ["id"] = id, ["name"] = "n", ["hashString"] = hash } };

    [Fact]
    public async Task A_409_is_repeated_with_the_session_id_and_the_id_is_reused()
    {
        var handler = new RpcHandler(17);
        var client = new TransmissionClient(handler);

        await client.PingAsync(Settings);
        Assert.Equal(1, handler.Conflicts);
        Assert.Equal([null, "abc123"], handler.Requests.Select(r => r.SessionId));
        Assert.All(handler.Requests, r => Assert.StartsWith("Basic ", r.Authorization));
        Assert.All(handler.Paths, p => Assert.Equal("/transmission/rpc", p));

        handler.Requests.Clear();
        await client.PingAsync(Settings);

        Assert.Equal(1, handler.Conflicts);
        Assert.Equal(["abc123"], handler.Requests.Select(r => r.SessionId));
    }

    [Fact]
    public async Task A_url_already_ending_in_rpc_is_used_as_given()
    {
        var handler = new RpcHandler(17);
        var client = new TransmissionClient(handler);

        await client.PingAsync(Settings with { Url = "http://tr.test:9091/custom/rpc/" });

        Assert.All(handler.Paths, p => Assert.Equal("/custom/rpc", p));
    }

    [Fact]
    public async Task Rpc_17_adds_with_labels_and_a_duplicate_returns_its_hash_lowercased()
    {
        var handler = new RpcHandler(17, (method, _) => method switch
        {
            "torrent-add" => Added("torrent-duplicate", "ABCDEF0123456789ABCDEF0123456789ABCDEF01"),
            "torrent-get" => new JsonObject { ["torrents"] = new JsonArray(new JsonObject { ["labels"] = new JsonArray("maki") }) },
            _ => null,
        });
        var client = new TransmissionClient(handler);

        var hash = await client.AddAsync(Settings, "magnet:?xt=urn:btih:abc");

        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", hash);
        var add = handler.Requests.Single(r => r.Method == "torrent-add");
        Assert.Equal("magnet:?xt=urn:btih:abc", add.Arguments["filename"]!.GetValue<string>());
        Assert.Equal(["maki"], add.Arguments["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.DoesNotContain(handler.Requests, r => r.Method == "torrent-set");
    }

    [Fact]
    public async Task A_duplicate_without_our_label_gets_it_added_next_to_the_users_labels()
    {
        var handler = new RpcHandler(17, (method, _) => method switch
        {
            "torrent-add" => Added("torrent-duplicate", "aa"),
            "torrent-get" => new JsonObject { ["torrents"] = new JsonArray(new JsonObject { ["labels"] = new JsonArray("keep") }) },
            _ => null,
        });
        var client = new TransmissionClient(handler);

        await client.AddFileAsync(Settings, [1, 2, 3], "aa");

        var add = handler.Requests.Single(r => r.Method == "torrent-add");
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), add.Arguments["metainfo"]!.GetValue<string>());
        var set = handler.Requests.Single(r => r.Method == "torrent-set");
        Assert.Equal(["keep", "maki"], set.Arguments["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
    }

    [Fact]
    public async Task Rpc_16_adds_without_labels_then_sets_them_on_the_returned_id()
    {
        var handler = new RpcHandler(16, (method, _) => method == "torrent-add" ? Added("torrent-added", "BB", id: 42) : null);
        var client = new TransmissionClient(handler);

        var hash = await client.AddAsync(Settings, "magnet:?xt=urn:btih:bb");

        Assert.Equal("bb", hash);
        var add = handler.Requests.Single(r => r.Method == "torrent-add");
        Assert.False(add.Arguments.ContainsKey("labels"));
        var set = handler.Requests.Single(r => r.Method == "torrent-set");
        Assert.Equal([42], set.Arguments["ids"]!.AsArray().Select(i => i!.GetValue<int>()));
        Assert.Equal(["maki"], set.Arguments["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.True(handler.Requests.ToList().IndexOf(set) > handler.Requests.ToList().IndexOf(add));
    }

    [Fact]
    public async Task Rpc_15_has_no_labels_so_nothing_is_labelled_and_listing_is_unfiltered()
    {
        var handler = new RpcHandler(15, (method, _) => method switch
        {
            "torrent-add" => Added("torrent-added", "DD", id: 3),
            "torrent-get" => new JsonObject
            {
                ["torrents"] = new JsonArray(new JsonObject
                {
                    ["hashString"] = "DD", ["name"] = "n", ["percentDone"] = 0.5, ["downloadDir"] = "/d",
                }),
            },
            _ => null,
        });
        var client = new TransmissionClient(handler);

        var hash = await client.AddAsync(Settings, "magnet:?xt=urn:btih:dd");
        var torrents = await client.ListAsync(Settings);

        Assert.Equal("dd", hash);
        Assert.False(handler.Requests.Single(r => r.Method == "torrent-add").Arguments.ContainsKey("labels"));
        Assert.DoesNotContain(handler.Requests, r => r.Method == "torrent-set");
        Assert.Equal("dd", Assert.Single(torrents).Hash);
    }

    [Fact]
    public async Task Rpc_18_still_receives_legacy_names_and_legacy_replies_parse()
    {
        var handler = new RpcHandler(18, (method, _) => method switch
        {
            "torrent-add" => Added("torrent-added", "CC"),
            "torrent-get" => new JsonObject
            {
                ["torrents"] = new JsonArray(new JsonObject
                {
                    ["hashString"] = "DD",
                    ["name"] = "Series v01",
                    ["percentDone"] = 1.0,
                    ["isFinished"] = true,
                    ["status"] = 6,
                    ["error"] = 0,
                    ["errorString"] = "",
                    ["downloadDir"] = "/downloads",
                    ["labels"] = new JsonArray("maki"),
                    ["addedDate"] = 1_700_000_000,
                }),
            },
            _ => null,
        });
        var client = new TransmissionClient(handler);

        var hash = await client.AddAsync(Settings, "magnet:?xt=urn:btih:cc");
        var list = await client.ListAsync(Settings);

        Assert.Equal("cc", hash);
        Assert.All(handler.Requests, r => Assert.DoesNotContain("_", r.Method));
        var add = handler.Requests.Single(r => r.Method == "torrent-add");
        Assert.Equal(["maki"], add.Arguments["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
        var get = handler.Requests.Single(r => r.Method == "torrent-get");
        var fields = get.Arguments["fields"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Contains("hashString", fields);
        Assert.Contains("percentDone", fields);
        Assert.DoesNotContain("hash_string", fields);

        var torrent = Assert.Single(list);
        Assert.Equal("dd", torrent.Hash);
        Assert.Equal(1.0, torrent.Progress);
        Assert.True(torrent.IsComplete);
        Assert.Equal("/downloads/Series v01", torrent.ContentPath);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), torrent.AddedAt);
    }

    [Fact]
    public async Task Listing_keeps_labelled_torrents_builds_content_paths_and_flags_errors()
    {
        static JsonObject T(string hash, string dir, string label, double done, int status, int error, string message) => new()
        {
            ["hashString"] = hash,
            ["name"] = "Name " + hash,
            ["percentDone"] = done,
            ["isFinished"] = false,
            ["status"] = status,
            ["error"] = error,
            ["errorString"] = message,
            ["downloadDir"] = dir,
            ["labels"] = new JsonArray(label),
            ["addedDate"] = 1_700_000_000,
        };

        var handler = new RpcHandler(16, (method, _) => method == "torrent-get"
            ? new JsonObject
            {
                ["torrents"] = new JsonArray(
                    T("AA", "/data/dl/", "MAKI", 1.0, 6, 0, ""),
                    T("BB", "D:\\torrents", "maki", 1.0, 6, 3, "No space left"),
                    T("CC", "/data", "other", 1.0, 6, 0, ""),
                    T("DD", "/data", "maki", 0.4, 4, 0, ""),
                    T("EE", "/data", "maki", 1.0, 2, 0, ""),
                    T("FF", "/data", "maki", 1.0, 6, 2, "Tracker gave an error")),
            }
            : null);
        var client = new TransmissionClient(handler);

        var list = await client.ListAsync(Settings);

        Assert.Equal(["aa", "bb", "dd", "ee", "ff"], list.Select(t => t.Hash));
        var ok = list[0];
        Assert.True(ok.IsComplete);
        Assert.False(ok.IsErrored);
        Assert.Null(ok.Message);
        Assert.Equal("/data/dl/Name AA", ok.ContentPath);

        var errored = list[1];
        Assert.True(errored.IsErrored);
        Assert.False(errored.IsComplete);
        Assert.Equal("No space left", errored.Message);
        Assert.Equal("D:\\torrents\\Name BB", errored.ContentPath);

        Assert.False(list[2].IsComplete);
        Assert.False(list[3].IsComplete);

        var trackerError = list[4];
        Assert.True(trackerError.IsComplete);
        Assert.False(trackerError.IsErrored);
        Assert.Equal("Tracker gave an error", trackerError.Message);
    }

    [Fact]
    public async Task A_401_throws_login_failed()
    {
        var client = new TransmissionClient(new StatusHandler(HttpStatusCode.Unauthorized, ""));

        var ex = await Assert.ThrowsAsync<TorrentClientException>(() => client.PingAsync(Settings));

        Assert.Equal(TorrentClientFailure.LoginFailed, ex.Failure);
    }

    [Fact]
    public async Task A_non_json_body_is_unreachable()
    {
        var client = new TransmissionClient(new StatusHandler(HttpStatusCode.OK, "<html>not transmission</html>"));

        var ex = await Assert.ThrowsAsync<TorrentClientException>(() => client.PingAsync(Settings));

        Assert.Equal(TorrentClientFailure.Unreachable, ex.Failure);
    }

    [Fact]
    public async Task A_failed_result_on_add_is_rejected()
    {
        var handler = new FailingAddHandler();
        var client = new TransmissionClient(handler);

        var ex = await Assert.ThrowsAsync<TorrentClientException>(() => client.AddAsync(Settings, "not a magnet"));

        Assert.Equal(TorrentClientFailure.Rejected, ex.Failure);
    }

    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class FailingAddHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["method"]!.GetValue<string>();
            var body = method == "session-get"
                ? """{"result":"success","arguments":{"rpc-version":17}}"""
                : """{"result":"invalid or corrupt torrent file","arguments":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
