using System.Net;
using System.Security.Cryptography;
using Mangarr.Core.Configuration;
using Mangarr.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ZstdSharp;

namespace Mangarr.Metadata.Tests;

public class MangaBakaDumpServiceTests : IDisposable
{
    private readonly string _workDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"mangarr-dump-test-{Guid.NewGuid():N}")).FullName;

    private readonly FakeAppSettings _settings = new();

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
        await File.WriteAllTextAsync(dbPath, "placeholder");
        _settings.Values[SettingKeys.MangaBakaDumpSha1] = "abc123";

        // Only the checksum endpoint is stubbed — requesting the dump itself would 404 and throw.
        var service = CreateService(new Dictionary<string, byte[]>
        {
            ["series.sqlite.zst.sha1"] = "abc123  series.sqlite.zst"u8.ToArray()
        });

        Assert.False(await service.RefreshAsync());
        Assert.Equal("placeholder", await File.ReadAllTextAsync(dbPath));
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
            _settings,
            NullLogger<MangaBakaLocalStore>.Instance);
        var results = await store.SearchAsync("one piece");
        Assert.Equal("377", Assert.Single(results).ProviderId);
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
