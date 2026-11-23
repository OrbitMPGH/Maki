using System.Collections.Concurrent;
using Maki.Sources.Common;
using Maki.Sources.TopManhua;

namespace Maki.Sources.Tests;

public class TopManhuaSourceTests
{
    private const string SeriesPage = """
        <div class="chapter-list">
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-226">Chapter 226: Seto-kun Can't Help but Make Her Study</a></div>
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-225">Anjou Anna Will Not Take the Easy Way Anymore</a></div>
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-12-5">Side Story</a></div>
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-222">Chapter 222</a></div>
        </div>
        """;

    [Fact]
    public async Task A_row_labelled_only_by_its_title_takes_the_number_from_the_url()
    {
        var source = new TopManhuaSource(new FakeHttpClientFactory(new() { ["manhua/anjo"] = SeriesPage }), null!);

        var chapters = await source.ListChaptersAsync("anjo");

        Assert.Equal([12.5m, 222m, 225m, 226m], chapters.Select(c => c.Number!.Value));
        var titled = chapters.Single(c => c.Number == 225m);
        Assert.Equal("Anjou Anna Will Not Take the Easy Way Anymore", titled.Title);
        Assert.Equal("chapter-225", titled.SourceChapterId);
        Assert.Null(chapters.Single(c => c.Number == 222m).Title);
    }

    [Fact]
    public async Task A_search_card_without_a_link_is_skipped_and_a_relative_one_resolves_against_the_site()
    {
        const string search = """
            <div class="c-tabs-item">
              <div><div class="post-title"><h3><a>No link</a></h3></div></div>
              <div><div class="post-title"><h3><a href="/manhua/relative">Relative</a></h3></div></div>
              <div><div class="post-title"><h3><a href="https://www.topmanhua.fan/manhua/anjo">Anjo</a></h3></div></div>
            </div>
            """;
        var source = new TopManhuaSource(new FakeHttpClientFactory(new() { ["?s="] = search }), null!);

        var results = await source.SearchAsync("anjo");

        Assert.Equal(["relative", "anjo"], results.Select(r => r.SourceSeriesId));
        Assert.All(results, r => Assert.StartsWith("https://www.topmanhua.fan/manhua/", r.Url));
    }

    [Fact]
    public async Task A_row_without_a_link_target_is_skipped_and_an_english_date_parses()
    {
        const string page = """
            <div class="chapter-list">
              <div><a>Chapter 3</a></div>
              <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-2">Chapter 2</a><span class="chapter-release-date">May 3, 2024</span></div>
            </div>
            """;
        var source = new TopManhuaSource(new FakeHttpClientFactory(new() { ["manhua/anjo"] = page }), null!);

        var chapter = Assert.Single(await source.ListChaptersAsync("anjo"));

        Assert.Equal(new DateTime(2024, 5, 3, 0, 0, 0, DateTimeKind.Utc), chapter.ReleaseDate);
    }

    [Fact]
    public async Task BuildStagedPages_reads_staged_files_lazily_handles_duplicates_and_cleans_up_on_dispose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maki-topmanhua-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p1 = Path.Combine(dir, "0001.bin");
        var p2 = Path.Combine(dir, "0002.bin");
        var pUnused = Path.Combine(dir, "0003.bin");
        await File.WriteAllBytesAsync(p1, [1, 2, 3]);
        await File.WriteAllBytesAsync(p2, [4, 5, 6, 7]);
        await File.WriteAllBytesAsync(pUnused, [8, 9]);

        var staged = new TopManhuaImageBrowser.StagedImages(dir, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://cdn.example/1.jpg"] = p1,
            ["https://cdn.example/dup.jpg"] = p2,
            ["https://cdn.example/unused.jpg"] = pUnused,
        });

        var urls = new[]
        {
            "https://cdn.example/1.jpg",
            "https://cdn.example/dup.jpg",
            "https://cdn.example/dup.jpg",
            "https://cdn.example/missing.jpg",
            "https://cdn.example/unused.jpg",
        };
        var headers = new Dictionary<string, string> { ["Referer"] = "https://www.topmanhua.fan/" };

        using (var pages = TopManhuaSource.BuildStagedPages(urls, headers, staged))
        {
            Assert.All(pages.Pages, p => Assert.Null(p.Data));
            Assert.NotNull(pages.Pages[0].Fetch);
            Assert.True(pages.Pages[0].SkipCooldown);
            Assert.Null(pages.Pages[3].Fetch);
            Assert.False(pages.Pages[3].SkipCooldown);

            // Unique staged file is deleted as soon as it is read.
            Assert.Equal([1, 2, 3], await pages.Pages[0].Fetch!(CancellationToken.None));
            Assert.False(File.Exists(p1));

            // Duplicate URL stays on disk until its last occurrence is read.
            Assert.Equal([4, 5, 6, 7], await pages.Pages[1].Fetch!(CancellationToken.None));
            Assert.True(File.Exists(p2));
            Assert.Equal([4, 5, 6, 7], await pages.Pages[2].Fetch!(CancellationToken.None));
            Assert.False(File.Exists(p2));

            // Unread staged file still exists until ChapterPages is disposed.
            Assert.True(File.Exists(pUnused));
        }

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Redirect_responses_are_not_treated_as_capture_failures()
    {
        Assert.False(TopManhuaImageBrowser.IsFailureStatus(200));
        Assert.False(TopManhuaImageBrowser.IsFailureStatus(301));
        Assert.False(TopManhuaImageBrowser.IsFailureStatus(302));
        Assert.False(TopManhuaImageBrowser.IsFailureStatus(307));
        Assert.False(TopManhuaImageBrowser.IsFailureStatus(308));
        Assert.True(TopManhuaImageBrowser.IsFailureStatus(403));
        Assert.True(TopManhuaImageBrowser.IsFailureStatus(404));
        Assert.True(TopManhuaImageBrowser.IsFailureStatus(500));
    }

    [Fact]
    public async Task Sliding_window_skips_blank_batches_and_advances_without_waiting_on_one_slow_image()
    {
        var urls = Enumerable.Range(1, 5).Select(i => $"https://cdn.example/{i}.jpg").ToArray();
        var wanted = new HashSet<string>(urls, StringComparer.Ordinal);
        var captured = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var failed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var domQueue = new Queue<string>(["", "  ", "", "", urls[0], urls[1], urls[2], urls[3], "https://cdn.example/ad.jpg", urls[4]]);
        var activated = new List<string>();
        var removed = new List<string>();
        var removedAll = false;

        Task<TopManhuaImageBrowser.ActivationBatch> Activate(int needed)
        {
            var batch = new List<string>();
            while (domQueue.Count > 0 && batch.Count < needed)
            {
                var next = domQueue.Dequeue().Trim();
                if (string.IsNullOrEmpty(next))
                {
                    continue;
                }

                batch.Add(next);
                activated.Add(next);
                // Page 1, 3, 4, 5 settle immediately; page 2 settles only after page 5 is activated.
                if (next == urls[4])
                {
                    captured[urls[4]] = "5.bin";
                    captured[urls[1]] = "2.bin";
                }
                else if (next != urls[1] && wanted.Contains(next))
                {
                    captured[next] = "ok.bin";
                }
            }

            return Task.FromResult(new TopManhuaImageBrowser.ActivationBatch([.. batch], domQueue.Count));
        }

        await TopManhuaImageBrowser.DriveSlidingWindowAsync(
            wanted,
            captured,
            failed,
            Activate,
            batch =>
            {
                removed.AddRange(batch);
                return Task.CompletedTask;
            },
            () =>
            {
                removedAll = true;
                return Task.CompletedTask;
            },
            imageTimeout: TimeSpan.FromSeconds(5),
            overallTimeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(10),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(5, captured.Count);
        Assert.Contains(urls[4], activated);
        Assert.Contains("https://cdn.example/ad.jpg", removed);
        Assert.True(removedAll);
    }

    [Fact]
    public void Sweep_skips_active_staging_directories_even_when_old()
    {
        var root = Path.Combine(Path.GetTempPath(), "maki-topmanhua-sweep-" + Guid.NewGuid().ToString("N"));
        var activeDir = Path.Combine(root, "active");
        var staleDir = Path.Combine(root, "stale");
        Directory.CreateDirectory(activeDir);
        Directory.CreateDirectory(staleDir);
        var oldTime = DateTime.UtcNow.AddHours(-3);
        Directory.SetLastWriteTimeUtc(activeDir, oldTime);
        Directory.SetLastWriteTimeUtc(staleDir, oldTime);

        try
        {
            using (var active = new TopManhuaImageBrowser.StagedImages(activeDir, new Dictionary<string, string>()))
            {
                TopManhuaImageBrowser.SweepStaleStagingDirs(root);

                Assert.True(Directory.Exists(activeDir));
                Assert.False(Directory.Exists(staleDir));
            }

            Assert.False(Directory.Exists(activeDir));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cancelled_browser_call_disposes_a_staged_result_that_completes_after_cancellation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maki-topmanhua-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "0001.bin"), [1, 2, 3]);
        var finish = new TaskCompletionSource<TopManhuaImageBrowser.StagedImages>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var call = CancellableBrowserCall.RunAsync(
            () => Task.CompletedTask,
            () => finish.Task,
            cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => call);

        // Even if the abandoned capture finishes successfully during its final drain, its directory is cleaned up.
        finish.TrySetResult(new TopManhuaImageBrowser.StagedImages(dir, new Dictionary<string, string>()));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Directory.Exists(dir) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(Directory.Exists(dir));
    }
}
