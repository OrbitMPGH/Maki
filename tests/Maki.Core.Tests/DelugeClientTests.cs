using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Download;

namespace Maki.Core.Tests;

public class DelugeClientTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private static readonly TorrentClientSettings Settings =
        new(TorrentClientKind.Deluge, "http://deluge.test:8112", "", "deluge", "Maki");

    private sealed record Reply(object? Result, int? ErrorCode = null, string ErrorMessage = "failed");

    private sealed class DelugeHandler : HttpMessageHandler
    {
        public List<(string Method, string Params)> Calls { get; } = [];
        public List<string> Methods => Calls.Select(c => c.Method).ToList();

        public Dictionary<string, Func<JsonElement, Reply>> Replies { get; } = new()
        {
            ["auth.login"] = _ => new Reply(true),
            ["web.connected"] = _ => new Reply(true),
            ["core.get_enabled_plugins"] = _ => new Reply(new[] { "Label" }),
            ["label.get_labels"] = _ => new Reply(new[] { "maki" }),
            ["core.enable_plugin"] = _ => new Reply(true),
            ["label.add"] = _ => new Reply(null),
            ["label.set_torrent"] = _ => new Reply(null),
            ["core.add_torrent_magnet"] = _ => new Reply(Hash),
            ["core.add_torrent_file"] = _ => new Reply(Hash),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/json", request.RequestUri!.AbsolutePath);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = doc.RootElement.GetProperty("method").GetString()!;
            var args = doc.RootElement.GetProperty("params").Clone();
            Calls.Add((method, args.GetRawText()));

            var reply = Replies.TryGetValue(method, out var responder)
                ? responder(args)
                : new Reply(null, 2, "Unknown method");
            var body = JsonSerializer.Serialize(new
            {
                result = reply.ErrorCode is null ? reply.Result : null,
                error = reply.ErrorCode is { } code ? new { code, message = reply.ErrorMessage } : null,
                id = doc.RootElement.GetProperty("id").GetInt32(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (DelugeClient Client, DelugeHandler Handler) Create()
    {
        var handler = new DelugeHandler();
        return (new DelugeClient(handler), handler);
    }

    [Fact]
    public async Task Login_happens_once_and_an_expired_session_logs_in_again_and_repeats_the_call()
    {
        var (client, handler) = Create();
        var updates = 0;
        handler.Replies["web.update_ui"] = _ => ++updates == 2
            ? new Reply(null, 1, "Not authenticated")
            : new Reply(new { torrents = new { } });

        await client.ListAsync(Settings);
        await client.ListAsync(Settings);

        Assert.Equal(
            [
                "auth.login", "web.connected",
                "core.get_enabled_plugins", "label.get_labels",
                "web.update_ui",
                "web.update_ui",
                "auth.login", "web.connected",
                "web.update_ui",
            ],
            handler.Methods);
    }

    [Fact]
    public async Task A_web_ui_without_a_daemon_connects_to_the_local_host_before_the_call()
    {
        var (client, handler) = Create();
        handler.Replies["web.connected"] = _ => new Reply(false);
        handler.Replies["web.get_hosts"] = _ => new Reply(new object[]
        {
            new object[] { "remote-id", "10.0.0.5", 58846, "Offline" },
            new object[] { "local-id", "127.0.0.1", 58846, "Online" },
        });
        handler.Replies["web.connect"] = _ => new Reply(null);

        await client.AddAsync(Settings, "magnet:?xt=urn:btih:" + Hash);

        Assert.Equal(
            ["auth.login", "web.connected", "web.get_hosts", "web.connect"],
            handler.Methods.Take(4));
        Assert.Equal("core.add_torrent_magnet", handler.Methods[4]);
        Assert.Equal("[\"local-id\"]", handler.Calls[3].Params);
    }

    [Fact]
    public async Task A_missing_label_plugin_is_enabled_and_the_lowercased_label_is_created_and_applied()
    {
        var (client, handler) = Create();
        handler.Replies["core.get_enabled_plugins"] = _ => new Reply(Array.Empty<string>());
        handler.Replies["label.get_labels"] = _ => new Reply(Array.Empty<string>());

        var hash = await client.AddAsync(Settings, "magnet:?xt=urn:btih:" + Hash);

        Assert.Equal(Hash, hash);
        Assert.Equal(
            [
                "auth.login", "web.connected", "core.add_torrent_magnet",
                "core.get_enabled_plugins", "core.enable_plugin", "label.get_labels",
                "label.get_labels", "label.add", "label.set_torrent",
            ],
            handler.Methods);
        Assert.Equal("[\"Label\"]", handler.Calls[4].Params);
        Assert.Equal("[\"maki\"]", handler.Calls[7].Params);
        Assert.Equal($"[\"{Hash}\",\"maki\"]", handler.Calls[8].Params);
    }

    [Fact]
    public async Task A_null_from_add_torrent_file_counts_as_added_and_the_computed_hash_is_labelled()
    {
        var (client, handler) = Create();
        handler.Replies["core.add_torrent_file"] = _ => new Reply(null);

        await client.AddFileAsync(Settings, [1, 2, 3], Hash.ToUpperInvariant());

        var add = handler.Calls.Single(c => c.Method == "core.add_torrent_file");
        Assert.Equal($"[\"{Hash.ToUpperInvariant()}.torrent\",\"AQID\",{{}}]", add.Params);
        var label = handler.Calls.Single(c => c.Method == "label.set_torrent");
        Assert.Equal($"[\"{Hash}\",\"maki\"]", label.Params);
    }

    [Fact]
    public async Task A_torrent_already_in_the_session_is_not_a_rejection()
    {
        var (client, handler) = Create();
        handler.Replies["core.add_torrent_file"] = _ => new Reply(null, 4, $"Torrent already in session ({Hash}).");

        await client.AddFileAsync(Settings, [1], Hash);

        Assert.Contains("label.set_torrent", handler.Methods);
    }

    [Fact]
    public async Task An_error_from_add_throws_rejected()
    {
        var (client, handler) = Create();
        handler.Replies["core.add_torrent_magnet"] = _ => new Reply(null, 3, "Invalid magnet");

        var ex = await Assert.ThrowsAsync<TorrentClientException>(
            () => client.AddAsync(Settings, "magnet:?xt=urn:btih:" + Hash));

        Assert.Equal(TorrentClientFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task Listing_maps_the_status_and_filters_by_label()
    {
        var (client, handler) = Create();
        JsonElement? sent = null;
        handler.Replies["web.update_ui"] = args =>
        {
            sent = args;
            return new Reply(new
            {
                torrents = new Dictionary<string, object>
                {
                    [Hash] = new
                    {
                        hash = Hash.ToUpperInvariant(),
                        name = "Series v01",
                        progress = 100.0,
                        state = "Seeding",
                        is_finished = true,
                        download_location = "/downloads",
                        move_completed = true,
                        move_completed_path = "/complete/",
                        time_added = 1700000000.5,
                        message = "OK",
                    },
                    ["feedfeedfeedfeedfeedfeedfeedfeedfeedfeed"] = new
                    {
                        hash = "feedfeedfeedfeedfeedfeedfeedfeedfeedfeed",
                        name = "Broken",
                        progress = 40.0,
                        state = "Error",
                        is_finished = false,
                        download_location = "C:\\dl",
                        move_completed = false,
                        move_completed_path = "",
                        time_added = 1700000100,
                        message = "Disk full",
                    },
                },
            });
        };

        var torrents = await client.ListAsync(Settings);

        Assert.Equal("{\"label\":\"maki\"}", sent!.Value[1].GetRawText());
        var done = torrents.Single(t => t.Hash == Hash);
        Assert.True(done.IsComplete);
        Assert.False(done.IsErrored);
        Assert.Equal(1.0, done.Progress);
        Assert.Equal("/complete/Series v01", done.ContentPath);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000500), done.AddedAt);
        var broken = torrents.Single(t => t.Name == "Broken");
        Assert.True(broken.IsErrored);
        Assert.False(broken.IsComplete);
        Assert.Equal("C:\\dl\\Broken", broken.ContentPath);
        Assert.Equal("Disk full", broken.Message);
    }

    [Fact]
    public async Task Listing_without_the_label_plugin_returns_every_torrent_unfiltered()
    {
        var (client, handler) = Create();
        handler.Replies["core.get_enabled_plugins"] = _ => new Reply(Array.Empty<string>());
        handler.Replies["core.enable_plugin"] = _ => new Reply(null, 3, "Plugin not found");
        JsonElement? sent = null;
        handler.Replies["web.update_ui"] = args =>
        {
            sent = args;
            return new Reply(new { torrents = new { } });
        };

        await client.ListAsync(Settings);
        await client.ListAsync(Settings);

        Assert.Equal("{}", sent!.Value[1].GetRawText());
        Assert.Single(handler.Methods, m => m == "core.enable_plugin");
    }

    [Fact]
    public async Task A_finished_torrent_still_moving_is_not_complete_until_the_move_ends()
    {
        var (client, handler) = Create();
        var state = "Moving";
        handler.Replies["web.update_ui"] = _ => new Reply(new
        {
            torrents = new Dictionary<string, object>
            {
                [Hash] = new
                {
                    hash = Hash,
                    name = "Series v01",
                    progress = 100.0,
                    state,
                    is_finished = true,
                    download_location = "/downloads",
                    move_completed = true,
                    move_completed_path = "/complete",
                    time_added = 1700000000,
                    message = "OK",
                },
            },
        });

        var moving = (await client.ListAsync(Settings)).Single();
        state = "Seeding";
        var seeding = (await client.ListAsync(Settings)).Single();

        Assert.False(moving.IsComplete);
        Assert.True(seeding.IsComplete);
        Assert.Equal("/complete/Series v01", seeding.ContentPath);
    }

    [Fact]
    public async Task A_torrent_that_cannot_be_labelled_fails_the_grab_and_listing_keeps_filtering()
    {
        var (client, handler) = Create();
        handler.Replies["label.set_torrent"] = _ => new Reply(null, 3, "Unknown torrent");
        JsonElement? sent = null;
        handler.Replies["web.update_ui"] = args =>
        {
            sent = args;
            return new Reply(new { torrents = new { } });
        };

        var ex = await Assert.ThrowsAsync<TorrentClientException>(
            () => client.AddAsync(Settings, "magnet:?xt=urn:btih:" + Hash));
        await client.ListAsync(Settings);

        Assert.Equal(TorrentClientFailure.Rejected, ex.Failure);
        Assert.Equal("{\"label\":\"maki\"}", sent!.Value[1].GetRawText());
    }

    [Fact]
    public async Task A_label_another_grab_created_first_is_used_rather_than_failing()
    {
        var (client, handler) = Create();
        var created = false;
        handler.Replies["label.get_labels"] = _ => new Reply(created ? new[] { "maki" } : Array.Empty<string>());
        handler.Replies["label.add"] = _ =>
        {
            created = true;
            return new Reply(null, 3, "Label already exists");
        };

        var hash = await client.AddAsync(Settings, "magnet:?xt=urn:btih:" + Hash);

        Assert.Equal(Hash, hash);
        Assert.Equal($"[\"{Hash}\",\"maki\"]", handler.Calls.Single(c => c.Method == "label.set_torrent").Params);
    }

    [Fact]
    public async Task An_invalid_label_lists_every_torrent_unfiltered()
    {
        var (client, handler) = Create();
        JsonElement? sent = null;
        handler.Replies["web.update_ui"] = args =>
        {
            sent = args;
            return new Reply(new { torrents = new { } });
        };
        var settings = Settings with { Label = "My Label!" };

        await client.AddAsync(settings, "magnet:?xt=urn:btih:" + Hash);
        await client.ListAsync(settings);

        Assert.Equal("{}", sent!.Value[1].GetRawText());
        Assert.DoesNotContain("label.set_torrent", handler.Methods);
    }

    [Fact]
    public async Task A_bad_password_throws_login_failed()
    {
        var (client, handler) = Create();
        handler.Replies["auth.login"] = _ => new Reply(false);

        var ex = await Assert.ThrowsAsync<TorrentClientException>(() => client.PingAsync(Settings));

        Assert.Equal(TorrentClientFailure.LoginFailed, ex.Failure);
    }

    [Fact]
    public async Task A_non_json_answer_is_unreachable()
    {
        var client = new DelugeClient(new HtmlHandler());

        var ex = await Assert.ThrowsAsync<TorrentClientException>(() => client.PingAsync(Settings));

        Assert.Equal(TorrentClientFailure.Unreachable, ex.Failure);
    }

    private sealed class HtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") });
    }
}
