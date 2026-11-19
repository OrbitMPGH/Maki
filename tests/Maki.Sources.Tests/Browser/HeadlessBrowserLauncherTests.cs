using Maki.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;
using PuppeteerSharp;

namespace Maki.Sources.Tests.Browser;

public class HeadlessBrowserLauncherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maki-launcher-").FullName;
    private readonly BrowserInstaller _installer;
    private int _drops;

    public HeadlessBrowserLauncherTests()
    {
        var directory = Path.Combine(_root, "chromium_headless_shell-test");
        var build = new BrowserInstaller.BrowserBuild(["https://fixture.test/shell.zip"], Path.Combine("shell", "headless_shell"), "");
        Directory.CreateDirectory(Path.Combine(directory, "shell"));
        File.WriteAllText(Path.Combine(directory, "shell", "headless_shell"), "");
        File.WriteAllText(Path.Combine(directory, "INSTALLATION_COMPLETE"), "");
        _installer = new BrowserInstaller(null, NullLogger.Instance, directory, build, TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        _installer.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private HeadlessBrowserLauncher Launcher(Func<LaunchOptions, Task<IBrowser>> launch, TimeSpan? closeTimeout = null) =>
        new(_installer, launch, () => Interlocked.Increment(ref _drops), closeTimeout ?? TimeSpan.FromSeconds(5));

    private static IBrowser Browser(Func<string, object?[]?, object?>? handle = null) => Fake<IBrowser>.Create(handle).Instance;

    [Fact]
    public async Task Launches_the_installed_shell_over_a_pipe_with_playwrights_flags()
    {
        LaunchOptions? seen = null;
        var launcher = Launcher(options =>
        {
            seen = options;
            return Task.FromResult(Browser());
        });

        await launcher.LaunchAsync(["--host-resolver-rules=MAP x y"]);

        Assert.NotNull(seen);
        Assert.Equal(_installer.Executable, seen.ExecutablePath);
        Assert.True(seen.Pipe);
        Assert.True(seen.IgnoreDefaultArgs);
        Assert.False(seen.WaitForInitialPage);
        Assert.Contains("--no-startup-window", seen.Args);
        Assert.Contains(BrowserSupport.QuoteArgument("--host-resolver-rules=MAP x y"), seen.Args);
        Assert.DoesNotContain("--enable-automation", seen.Args);
    }

    [Theory]
    [InlineData("--no-sandbox", false, "--no-sandbox")]
    [InlineData("--disable-features=A,B", false, "--disable-features=A,B")]
    [InlineData("--host-resolver-rules=MAP x 127.0.0.1", false, "'--host-resolver-rules=MAP x 127.0.0.1'")]
    [InlineData("--rules=a;rm -rf /", false, "'--rules=a;rm -rf /'")]
    [InlineData("--rules=it's", false, "'--rules=it'\\''s'")]
    [InlineData("--host-resolver-rules=MAP x 127.0.0.1", true, "\"--host-resolver-rules=MAP x 127.0.0.1\"")]
    [InlineData("--path=C:\\a b\\", true, "\"--path=C:\\a b\\\\\"")]
    [InlineData("--say=\"hi\"", true, "\"--say=\\\"hi\\\"\"")]
    public void A_launch_argument_with_spaces_or_shell_characters_stays_one_argument(string argument, bool windows, string quoted) =>
        Assert.Equal(quoted, BrowserSupport.QuoteArgument(argument, windows));

    [Fact]
    public async Task The_page_cache_drops_only_when_the_last_browser_closes()
    {
        var launcher = Launcher(_ => Task.FromResult(Browser()));
        var first = await launcher.LaunchAsync();
        var second = await launcher.LaunchAsync();
        Assert.Equal(2, launcher.Running);

        await launcher.CloseAsync(first);
        Assert.Equal(1, launcher.Running);
        Assert.Equal(0, _drops);

        await launcher.CloseAsync(second);
        Assert.Equal(0, launcher.Running);
        Assert.Equal(1, _drops);
    }

    [Fact]
    public async Task A_browser_starting_holds_the_count_while_another_closes()
    {
        var starting = new TaskCompletionSource<IBrowser>();
        var calls = 0;
        var launcher = Launcher(_ => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(Browser()) : starting.Task);
        var running = await launcher.LaunchAsync();
        var launch = launcher.LaunchAsync();

        await launcher.CloseAsync(running);

        Assert.Equal(1, launcher.Running);
        Assert.Equal(0, _drops);
        starting.SetResult(Browser());
        await launcher.CloseAsync(await launch);
        Assert.Equal(1, _drops);
    }

    [Fact]
    public async Task A_failed_launch_is_counted_out()
    {
        var launcher = Launcher(_ => Task.FromException<IBrowser>(new ProcessException("Failed to launch browser")));

        await Assert.ThrowsAsync<ProcessException>(() => launcher.LaunchAsync());

        Assert.Equal(0, launcher.Running);
    }

    [Fact]
    public async Task A_browser_whose_close_throws_or_hangs_is_still_counted_out_and_disposed()
    {
        var throwing = Fake<IBrowser>.Create((name, _) =>
            name == nameof(IBrowser.CloseAsync) ? Task.FromException(new TargetClosedException("gone")) : null);
        var hanging = Fake<IBrowser>.Create((name, _) =>
            name == nameof(IBrowser.CloseAsync) ? new TaskCompletionSource().Task : null);
        var queue = new Queue<IBrowser>([throwing.Instance, hanging.Instance]);
        var launcher = Launcher(_ => Task.FromResult(queue.Dequeue()), closeTimeout: TimeSpan.FromMilliseconds(100));
        var first = await launcher.LaunchAsync();
        var second = await launcher.LaunchAsync();

        await launcher.CloseAsync(first);
        await launcher.CloseAsync(second);

        Assert.Equal(0, launcher.Running);
        Assert.Equal(1, _drops);
        Assert.Contains(nameof(IDisposable.Dispose), throwing.Proxy.Calls);
        Assert.Contains(nameof(IDisposable.Dispose), hanging.Proxy.Calls);
    }

    [Fact]
    public async Task Closing_a_browser_twice_counts_it_out_once()
    {
        var launcher = Launcher(_ => Task.FromResult(Browser()));
        var first = await launcher.LaunchAsync();
        var second = await launcher.LaunchAsync();

        await launcher.CloseAsync(first);
        await launcher.CloseAsync(first);

        Assert.Equal(1, launcher.Running);
        Assert.Equal(0, _drops);
        await launcher.CloseAsync(second);
    }
}
