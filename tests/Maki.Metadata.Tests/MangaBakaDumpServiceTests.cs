using System.Net;
using System.Security.Cryptography;
using Maki.Core.Configuration;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ZstdSharp;

namespace Maki.Metadata.Tests;

public class MangaBakaDumpServiceTests : IDisposable
{
    private readonly string _workDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"maki-dump-test-{Guid.NewGuid():N}")).FullName;

    private readonly FakeAppSettings _settings = new();

    private readonly MangaBakaDumpStatus _status = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private MangaBakaDumpService CreateService(Dictionary<string, byte[]> responses) => new(
        new FakeDumpHttpClientFactory(responses),
        new MangaBakaDumpOptions(Path.Combine(_workDir, "mangabaka.db"), _workDir),
        _settings,
        _status,
        NullLogger<MangaBakaDumpService>.Instance);

    private static (byte[] Compressed, string Sha1) CompressDb(string dbPath)
    {
        using var output = new MemoryStream();
        using (var input = File.OpenRead(dbPath))
        using (var compressor = new CompressionStream(output))
        {
            input.CopyTo(compressor);
        }

        var bytes = output.ToArray();
        return (bytes, Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    [Fact]
    public async Task Refresh_skips_download_when_checksum_unchanged()
    {
        var dbPath = Path.Combine(_workDir, "mangabaka.db");
        using (var installed = new DumpDbBuilder())
        {
            installed.AddSeries(377, "ONE PIECE").BuildSearchIndex();
            SqliteConnection.ClearAllPools();
            File.Copy(installed.Path, dbPath);
        }

        var before = await File.ReadAllBytesAsync(dbPath);
        _settings.Values[SettingKeys.MangaBakaDumpSha1] = "abc123";

        // Only the checksum endpoint is stubbed: requesting the dump itself would 404 and throw.
        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = "abc123  series.sqlite.zst"u8.ToArray()
        });

        Assert.False(await service.RefreshAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(dbPath));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("no-search-table")]
    public async Task Refresh_replaces_a_damaged_dump_even_when_the_checksum_is_unchanged(string damage)
    {
        var dbPath = Path.Combine(_workDir, "mangabaka.db");
        using (var installed = new DumpDbBuilder())
        {
            installed.AddSeries(1, "Old").AddFillerSeries(2000);
            if (damage != "no-search-table")
            {
                installed.BuildSearchIndex();
            }

            SqliteConnection.ClearAllPools();
            File.Copy(installed.Path, dbPath);
        }

        if (damage == "garbage")
        {
            await File.WriteAllTextAsync(dbPath, "placeholder");
        }
        else if (damage == "truncated")
        {
            await using var file = new FileStream(dbPath, FileMode.Open);
            file.SetLength(file.Length / 2);
        }

        Assert.NotNull(MangaBakaDumpService.IntegrityProblem(dbPath));

        using var source = new DumpDbBuilder();
        source.AddSeries(377, "ONE PIECE").AddFillerSeries(1000);
        var (compressed, sha1) = CompressDb(source.Path);
        _settings.Values[SettingKeys.MangaBakaDumpSha1] = sha1;
        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = System.Text.Encoding.UTF8.GetBytes($"{sha1}  series.sqlite.zst"),
            ["series.sqlite.zst"] = compressed
        });

        Assert.True(await service.RefreshAsync());
        Assert.Null(MangaBakaDumpService.IntegrityProblem(dbPath));
        Assert.False(service.Damaged);
    }

    [Fact]
    public async Task A_damaged_dump_whose_download_fails_stays_flagged()
    {
        var dbPath = Path.Combine(_workDir, "mangabaka.db");
        await File.WriteAllTextAsync(dbPath, "placeholder");
        _settings.Values[SettingKeys.MangaBakaDumpSha1] = "abc123";

        // The dump itself is not stubbed, so the re-download 404s.
        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = "abc123  series.sqlite.zst"u8.ToArray()
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshAsync());

        Assert.True(service.Damaged);
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public async Task Refresh_downloads_verifies_indexes_and_swaps()
    {
        using var source = new DumpDbBuilder();
        source.AddSeries(377, "ONE PIECE").AddFillerSeries(1000);
        var (compressed, sha1) = CompressDb(source.Path);

        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = System.Text.Encoding.UTF8.GetBytes($"{sha1}  series.sqlite.zst"),
            ["series.sqlite.zst"] = compressed
        });

        Assert.True(await service.RefreshAsync());

        var installedPath = Path.Combine(_workDir, "mangabaka.db");
        Assert.True(File.Exists(installedPath));
        Assert.False(File.Exists(Path.Combine(_workDir, "mangabaka.db.partial")));
        Assert.Equal(sha1, _settings.Values[SettingKeys.MangaBakaDumpSha1]);
        Assert.NotNull(_settings.Values[SettingKeys.MangaBakaDumpRefreshedAt]);

        // The installed copy is queryable through the FTS index the service built.
        var store = new MangaBakaLocalStore(
            new MangaBakaDumpOptions(installedPath, _workDir),
            NullLogger<MangaBakaLocalStore>.Instance);
        var results = await store.SearchAsync("one piece", ContentRating.Pornographic);
        Assert.Equal("377", Assert.Single(results).ProviderId);

        var progress = _status.Snapshot();
        Assert.False(progress.Running);
        Assert.Equal("idle", progress.Phase);
        Assert.True(progress.LastInstalled);
        Assert.Null(progress.LastError);
        Assert.Equal(compressed.LongLength, progress.DownloadedBytes);
    }

    [Fact]
    public async Task Refresh_names_an_empty_checksum_response()
    {
        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = "   "u8.ToArray()
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync());

        Assert.Contains("checksum response", ex.Message);
    }

    [Fact]
    public async Task Refresh_removes_the_old_files_journal_sidecars_before_swapping()
    {
        var installedPath = Path.Combine(_workDir, "mangabaka.db");
        await File.WriteAllTextAsync(installedPath, "old");
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            await File.WriteAllTextAsync(installedPath + suffix, "stale");
        }

        using var source = new DumpDbBuilder();
        source.AddSeries(377, "ONE PIECE").AddFillerSeries(1000);
        var (compressed, sha1) = CompressDb(source.Path);
        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = System.Text.Encoding.UTF8.GetBytes($"{sha1}  series.sqlite.zst"),
            ["series.sqlite.zst"] = compressed
        });

        Assert.True(await service.RefreshAsync());

        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            Assert.False(File.Exists(installedPath + suffix), suffix);
        }
    }

    [Fact]
    public async Task Refresh_reports_an_HttpClient_timeout_as_a_failure_not_a_shutdown()
    {
        var service = new MangaBakaDumpService(
            new HangingDumpHttpClientFactory("abc123  series.sqlite.zst"),
            new MangaBakaDumpOptions(Path.Combine(_workDir, "mangabaka.db"), _workDir),
            _settings,
            _status,
            NullLogger<MangaBakaDumpService>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(() => service.RefreshAsync());

        var progress = _status.Snapshot();
        Assert.False(progress.Running);
        Assert.Contains("timed out", progress.LastError);
    }

    [Fact]
    public async Task Refresh_cancelled_by_its_caller_is_not_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var service = new MangaBakaDumpService(
            new HangingDumpHttpClientFactory("abc123  series.sqlite.zst", TimeSpan.FromMinutes(5), cts.Cancel),
            new MangaBakaDumpOptions(Path.Combine(_workDir, "mangabaka.db"), _workDir),
            _settings,
            _status,
            NullLogger<MangaBakaDumpService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cts.Token));

        Assert.Null(_status.Snapshot().LastError);
    }

    [Fact]
    public async Task Refresh_cancelled_mid_download_stops_promptly_and_removes_the_staged_file()
    {
        using var source = new DumpDbBuilder();
        source.AddSeries(377, "ONE PIECE").AddFillerSeries(1000);
        var (compressed, sha1) = CompressDb(source.Path);

        using var cts = new CancellationTokenSource();
        var service = new MangaBakaDumpService(
            new DribblingDumpHttpClientFactory($"{sha1}  series.sqlite.zst", compressed, cts.Cancel),
            new MangaBakaDumpOptions(Path.Combine(_workDir, "mangabaka.db"), _workDir),
            _settings,
            _status,
            NullLogger<MangaBakaDumpService>.Instance);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cts.Token));

        // The whole body would take well over ten seconds at the dribble rate.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.False(File.Exists(Path.Combine(_workDir, "mangabaka.db.partial")));
        Assert.Null(_status.Snapshot().LastError);
    }

    [Fact]
    public void Interruptible_stops_a_running_statement_when_cancelled()
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_workDir, "slow.db")};Pooling=False");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (x INTEGER)";
            create.ExecuteNonQuery();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // One statement inside a transaction, the shape of the FTS build. Runs for minutes uninterrupted.
        Assert.ThrowsAny<OperationCanceledException>(() => MangaBakaDumpService.Interruptible(conn, cts.Token, () =>
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO t
                WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c LIMIT 2000000000)
                SELECT x FROM c
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");

        // The handler is removed again, so the connection stays usable.
        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public async Task Refresh_removes_a_staged_file_left_by_a_killed_run()
    {
        var dbPath = Path.Combine(_workDir, "mangabaka.db");
        using (var installed = new DumpDbBuilder())
        {
            installed.AddSeries(377, "ONE PIECE").BuildSearchIndex();
            SqliteConnection.ClearAllPools();
            File.Copy(installed.Path, dbPath);
        }

        var staged = Path.Combine(_workDir, "mangabaka.db.partial");
        await File.WriteAllTextAsync(staged, "half a dump");
        await File.WriteAllTextAsync(staged + "-journal", "journal");
        _settings.Values[SettingKeys.MangaBakaDumpSha1] = "abc123";
        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = "abc123  series.sqlite.zst"u8.ToArray()
        });

        Assert.False(await service.RefreshAsync());

        Assert.False(File.Exists(staged));
        Assert.False(File.Exists(staged + "-journal"));
    }

    [Fact]
    public async Task Refresh_rejects_checksum_mismatch_and_cleans_up()
    {
        using var source = new DumpDbBuilder();
        source.AddFillerSeries(1000);
        var (compressed, _) = CompressDb(source.Path);

        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = "0000000000000000000000000000000000000000  series.sqlite.zst"u8.ToArray(),
            ["series.sqlite.zst"] = compressed
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync());

        Assert.False(File.Exists(Path.Combine(_workDir, "mangabaka.db")));
        Assert.False(File.Exists(Path.Combine(_workDir, "mangabaka.db.partial")));
        Assert.False(_settings.Values.ContainsKey(SettingKeys.MangaBakaDumpSha1));

        // The failure has to reach the status object, or the toast sits on its last download frame
        // and the settings card never explains why nothing arrived.
        var progress = _status.Snapshot();
        Assert.False(progress.Running);
        Assert.False(progress.LastInstalled);
        Assert.Contains("checksum mismatch", progress.LastError);
    }
}

/// <summary>IHttpClientFactory whose clients answer from canned byte responses keyed by URL suffix.</summary>
public class FakeDumpHttpClientFactory(Dictionary<string, byte[]> responsesByUrlSuffix) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(new FakeHandler(responsesByUrlSuffix))
        {
            BaseAddress = new Uri("https://fixture.test/")
        };
    }

    private class FakeHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            foreach (var (suffix, body) in responses)
            {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(body)
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fixture for {url}")
            });
        }
    }
}

/// <summary>
/// Answers the checksum, then never answers the dump request, so the client's own
/// <see cref="HttpClient.Timeout"/> fires, or <paramref name="onHang"/> cancels from outside.
/// </summary>
public class HangingDumpHttpClientFactory(string sha1Line, TimeSpan? timeout = null, Action? onHang = null)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Handler(sha1Line, onHang))
    {
        BaseAddress = new Uri("https://fixture.test/"),
        Timeout = timeout ?? TimeSpan.FromMilliseconds(200),
    };

    private class Handler(string sha1Line, Action? onHang) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.ToString().EndsWith(".sha1", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha1Line) };
            }

            onHang?.Invoke();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}

/// <summary>
/// Answers the checksum, then serves the dump a kilobyte at a time with a pause between reads, calling
/// <paramref name="onFirstRead"/> once the first chunk is out so a test can cancel mid-body.
/// </summary>
public class DribblingDumpHttpClientFactory(string sha1Line, byte[] body, Action onFirstRead) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Handler(sha1Line, body, onFirstRead))
    {
        BaseAddress = new Uri("https://fixture.test/"),
    };

    private class Handler(string sha1Line, byte[] body, Action onFirstRead) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpContent content = request.RequestUri!.ToString().EndsWith(".sha1", StringComparison.OrdinalIgnoreCase)
                ? new StringContent(sha1Line)
                : new StreamContent(new DribbleStream(body, onFirstRead));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private class DribbleStream(byte[] body, Action onFirstRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position > 0)
            {
                await Task.Delay(250, ct);
            }

            var read = Math.Min(Math.Min(buffer.Length, 1024), body.Length - _position);
            body.AsMemory(_position, read).CopyTo(buffer);
            if (_position == 0)
            {
                onFirstRead();
            }

            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        // Synchronous reads would bypass the token, which is the thing under test.
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
