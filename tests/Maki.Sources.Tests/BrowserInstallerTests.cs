using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Maki.Sources.Common;

namespace Maki.Sources.Tests;

public class BrowserInstallerTests
{
    [Theory]
    [InlineData("windows", Architecture.X64, "win64/chrome-headless-shell-win64.zip", "chrome-headless-shell-win64/chrome-headless-shell.exe")]
    [InlineData("linux", Architecture.X64, "linux64/chrome-headless-shell-linux64.zip", "chrome-headless-shell-linux64/chrome-headless-shell")]
    [InlineData("osx", Architecture.Arm64, "mac-arm64/chrome-headless-shell-mac-arm64.zip", "chrome-headless-shell-mac-arm64/chrome-headless-shell")]
    public void Chrome_for_testing_platforms_download_the_pinned_version(
        string os, Architecture arch, string file, string executable)
    {
        var build = BrowserInstaller.Build(OSPlatform.Create(os.ToUpperInvariant()), arch);

        Assert.NotNull(build);
        Assert.All(build.Urls, url => Assert.EndsWith($"/{BrowserInstaller.Version}/{file}", url));
        Assert.Equal(executable.Replace('/', Path.DirectorySeparatorChar), build.Executable);
    }

    /// <summary>Chrome for Testing publishes nothing for Linux arm64, which is the NAS case the Docker
    /// image's second platform covers, so that one comes from Playwright's own build by revision.</summary>
    [Fact]
    public void Linux_arm64_downloads_playwrights_build_by_revision()
    {
        var build = BrowserInstaller.Build(OSPlatform.Linux, Architecture.Arm64);

        Assert.NotNull(build);
        Assert.All(build.Urls, url =>
            Assert.EndsWith($"/builds/chromium/{BrowserInstaller.Revision}/chromium-headless-shell-linux-arm64.zip", url));
        Assert.Equal(Path.Combine("chrome-linux", "headless_shell"), build.Executable);
    }

    [Fact]
    public void A_platform_with_no_build_has_none()
    {
        Assert.Null(BrowserInstaller.Build(OSPlatform.Windows, Architecture.Arm64));
    }

    /// <summary>
    /// The image downloads the browser itself at build time, and an install made by an older build is
    /// reused only while the folder name agrees, so the two pins must move together.
    /// </summary>
    [Fact]
    public void The_docker_image_pins_the_same_browser()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "distribution", "docker", "Dockerfile"));

        Assert.Equal(BrowserInstaller.Version, Regex.Match(dockerfile, @"ARG BROWSER_VERSION=(\S+)").Groups[1].Value);
        Assert.Equal(BrowserInstaller.Revision, Regex.Match(dockerfile, @"ARG BROWSER_REVISION=(\S+)").Groups[1].Value);
        Assert.Equal(BrowserInstaller.Build(OSPlatform.Linux, Architecture.X64)!.Sha256,
            Regex.Match(dockerfile, @"ARG BROWSER_SHA256_AMD64=(\S+)").Groups[1].Value);
        Assert.Equal(BrowserInstaller.Build(OSPlatform.Linux, Architecture.Arm64)!.Sha256,
            Regex.Match(dockerfile, @"ARG BROWSER_SHA256_ARM64=(\S+)").Groups[1].Value);
    }

    [Theory]
    [InlineData("windows", Architecture.X64)]
    [InlineData("linux", Architecture.X64)]
    [InlineData("linux", Architecture.Arm64)]
    [InlineData("osx", Architecture.X64)]
    [InlineData("osx", Architecture.Arm64)]
    public void Every_build_carries_a_checksum(string os, Architecture arch) =>
        Assert.Matches("^[0-9a-f]{64}$", BrowserInstaller.Build(OSPlatform.Create(os.ToUpperInvariant()), arch)!.Sha256);

    [Fact]
    public void An_install_lands_in_the_folder_playwright_used()
    {
        Assert.Equal($"chromium_headless_shell-{BrowserInstaller.Revision}", Path.GetFileName(BrowserInstaller.BrowserDirectory()));
        Assert.Equal(Path.Combine(BrowserInstaller.BrowserDirectory()!, "INSTALLATION_COMPLETE"), BrowserInstaller.InstallMarker());
    }

    [Fact]
    public void Unpacking_keeps_only_the_english_locale_and_needs_the_executable()
    {
        using var temp = new TempDir();
        var zip = Path.Combine(temp.Path, "shell.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "shell/headless_shell", "shell/locales/en-US.pak", "shell/locales/fr.pak", "shell/resources.pak" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(name);
            }
        }

        var staging = Path.Combine(temp.Path, "out");
        BrowserInstaller.Unpack(zip, staging, Path.Combine("shell", "headless_shell"));

        Assert.True(File.Exists(Path.Combine(staging, "shell", "locales", "en-US.pak")));
        Assert.False(File.Exists(Path.Combine(staging, "shell", "locales", "fr.pak")));
        Assert.True(File.Exists(Path.Combine(staging, "shell", "resources.pak")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(staging, "shell", "headless_shell")).HasFlag(UnixFileMode.OtherExecute));
        }

        Assert.Throws<BrowserUnavailableException>(() =>
            BrowserInstaller.Unpack(zip, Path.Combine(temp.Path, "again"), Path.Combine("shell", "missing")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Maki.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("maki-browser-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
