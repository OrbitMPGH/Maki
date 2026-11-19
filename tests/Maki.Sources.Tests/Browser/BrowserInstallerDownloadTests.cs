using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Maki.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Sources.Tests.Browser;

public class BrowserInstallerDownloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maki-install-").FullName;
    private readonly byte[] _zip = ShellZip();
    private readonly MirrorHandler _mirrors;

    public BrowserInstallerDownloadTests()
    {
        _mirrors = new MirrorHandler(_zip);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Target => Path.Combine(_root, "chromium_headless_shell-test");

    private BrowserInstaller Installer(params string[] mirrors) => Installer(TimeSpan.FromMilliseconds(300), mirrors);

    private BrowserInstaller Installer(TimeSpan stallTimeout, params string[] mirrors) => new(
        new Factory(_mirrors),
        NullLogger.Instance,
        Target,
        new BrowserInstaller.BrowserBuild(
            mirrors.Select(m => $"https://{m}.test/shell.zip").ToArray(),
            Path.Combine("shell", "headless_shell"),
            Convert.ToHexStringLower(SHA256.HashData(_zip))),
        stallTimeout);

    [Fact]
    public async Task Each_mirror_that_fails_stalls_truncates_or_sends_other_bytes_gives_way_to_the_next()
    {
        using var installer = Installer("broken", "stalled", "truncated", "tampered", "good");

        await installer.EnsureInstalledAsync();

        Assert.True(installer.IsInstalled());
        Assert.True(File.Exists(Path.Combine(Target, "INSTALLATION_COMPLETE")));
        Assert.True(File.Exists(installer.Executable));
        Assert.Equal(["broken", "stalled", "truncated", "tampered", "good"], _mirrors.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root).Where(p => p.EndsWith(".zip") || p.EndsWith(".tmp")));
    }

    [Fact]
    public async Task No_mirror_delivering_leaves_no_browser_behind()
    {
        using var installer = Installer("broken", "truncated", "tampered");

        var ex = await Assert.ThrowsAsync<BrowserUnavailableException>(() => installer.EnsureInstalledAsync());

        Assert.Contains("SHA-256", ex.Message);
        Assert.Equal(BrowserInstallState.Failed, installer.State);
        Assert.False(Directory.Exists(Target));
        Assert.False(installer.IsInstalled());
    }

    [Fact]
    public async Task An_unfinished_install_without_its_marker_is_replaced()
    {
        Directory.CreateDirectory(Path.Combine(Target, "shell"));
        File.WriteAllText(Path.Combine(Target, "leftover"), "half an unzip");
        using var installer = Installer("good");

        await installer.EnsureInstalledAsync();

        Assert.True(installer.IsInstalled());
        Assert.False(File.Exists(Path.Combine(Target, "leftover")));
    }

    /// <summary>
    /// Two processes sharing the folder (an installed build and a development checkout). Each one
    /// is its own installer here; only the lock file stands between them.
    /// </summary>
    [Fact]
    public async Task A_second_installer_on_the_same_folder_waits_and_reuses_the_first_ones_install()
    {
        _mirrors.Delay = TimeSpan.FromMilliseconds(500);
        using var first = Installer(TimeSpan.FromSeconds(30), "good");
        using var second = Installer(TimeSpan.FromSeconds(30), "good");

        await Task.WhenAll(first.EnsureInstalledAsync(), second.EnsureInstalledAsync());

        Assert.True(first.IsInstalled());
        Assert.True(second.IsInstalled());
        Assert.Single(_mirrors.Requests);
    }

    private static byte[] ShellZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "shell/headless_shell", "shell/locales/en-US.pak", "shell/locales/de.pak" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(name);
            }
        }

        return memory.ToArray();
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MirrorHandler(byte[] zip) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var mirror = request.RequestUri!.Host.Split('.')[0];
            lock (Requests)
            {
                Requests.Add(mirror);
            }

            await Task.Delay(Delay, ct);
            return mirror switch
            {
                "broken" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "stalled" => Body(new StallingStream(zip[..100]), zip.Length),
                "truncated" => Body(new MemoryStream(zip[..(zip.Length / 2)]), zip.Length),
                "tampered" => Body(new MemoryStream(zip.Reverse().ToArray()), zip.Length),
                _ => Body(new MemoryStream(zip), zip.Length),
            };
        }

        private static HttpResponseMessage Body(Stream stream, long length)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentLength = length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>Sends a few bytes and then nothing, the way a CDN can after a 200.</summary>
    private sealed class StallingStream(byte[] head) : MemoryStream(head)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await base.ReadAsync(buffer, ct);
            if (n > 0)
            {
                return n;
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}
