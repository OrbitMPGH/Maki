using Jeffijoe.MessageFormat;
using Maki.Api.Localization;
using Maki.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Renders every notification message against the real English catalogue.
/// <para>
/// <see cref="LocalizationCatalogTests"/> proves the keys exist and that placeholder sets match. It
/// does not prove the ICU inside them is valid, and these are the most intricate messages in the
/// catalogue: several carry <c>=0 {}</c> branches so a zero count drops out of the sentence, and two
/// use a <c>select</c> to omit a clause. A malformed one of those does not throw at startup or fail
/// a build. It surfaces as a notification that reads as the raw key, or as a sentence with a literal
/// <c>{count}</c> in it, weeks later, in somebody's bell.
/// </para>
/// </summary>
public class InboxRendererTests
{
    private static readonly InboxRenderer Renderer = new(
        new Localizer(
            new ServerCatalogs(NullLogger<ServerCatalogs>.Instance),
            new MessageFormatter(),
            new FixedLocale(SupportedLanguages.Default),
            NullLogger<Localizer>.Instance));

    /// <summary>
    /// One representative set of parameters per message. Kept together so adding a notification
    /// without adding a case here is a failing test rather than a silent gap.
    /// </summary>
    public static TheoryData<string, object> Messages() => new()
    {
        { "inbox.libraryImport.finished", new { imported = 3, failed = 0 } },
        { "inbox.libraryImport.finishedWithErrors", new { imported = 3, failed = 2 } },
        { "inbox.request.submitted", new { user = "alice", title = "Berserk" } },
        { "inbox.request.edited", new { title = "Berserk", range = "1-20" } },
        { "inbox.request.approvedQueued", new { title = "Berserk", queued = 4 } },
        { "inbox.request.approvedInLibrary", new { title = "Berserk", queued = 0 } },
        { "inbox.request.declined", new { title = "Berserk" } },
        { "inbox.chapters.available", new { count = 2 } },
        { "inbox.chapters.queued", new { count = 1 } },
        { "inbox.backup.preUpgrade", new { name = "maki-2026.db", count = 3 } },
        { "inbox.achievement.unlocked", new { achievement = "reader", tier = 3 } },
        { "inbox.levelUp", new { level = 4, previous = 3 } },
        { "inbox.levelUpJump", new { level = 6, previous = 3 } },
        { "inbox.chapter.downloaded", new { chapter = "12.5" } },
        { "inbox.download.failed", new { hasChapter = "yes", chapter = "12.5", error = "timed out" } },
        { "inbox.smartDownload.queued", new { count = 5 } },
        { "inbox.chapters.downloaded", new { count = 5 } },
        {
            "inbox.downloads.finishedWithErrors",
            new { completed = 3, failed = 1, cancelled = 0, unfinished = 0, queued = 4, hasError = "yes", error = "429" }
        },
        { "inbox.sourceMatch.matched", new { sources = "MangaDex, MangaPill" } },
        { "inbox.sourceMatch.none", new { } },
        { "inbox.update.available", new { latest = "1.2.0", current = "1.1.0" } },
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Every_message_renders(string key, object args)
    {
        var (title, body) = Render(key, args);

        foreach (var (what, text) in new[] { ("title", title), ("body", body) })
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{key}.{what} rendered empty");
            Assert.DoesNotContain($"{key}.{what}", text, StringComparison.Ordinal);
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("}", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_zero_count_drops_out_of_the_batch_summary()
    {
        var (_, body) = Render(
            "inbox.downloads.finishedWithErrors",
            new { completed = 3, failed = 1, cancelled = 0, unfinished = 0, queued = 4, hasError = "no", error = (string?)null });

        Assert.Contains("1 failed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("0 cancelled", body, StringComparison.Ordinal);
        Assert.DoesNotContain("0 unfinished", body, StringComparison.Ordinal);
        Assert.DoesNotContain("First error", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_with_no_chapter_names_none()
    {
        var (_, body) = Render(
            "inbox.download.failed",
            new { hasChapter = "no", chapter = (string?)null, error = "timed out" });

        Assert.DoesNotContain("chapter", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ungraded_achievement_names_no_tier()
    {
        var (_, graded) = Render("inbox.achievement.unlocked", new { achievement = "reader", tier = 2 });
        var (_, plain) = Render("inbox.achievement.unlocked", new { achievement = "first-page", tier = 0 });

        Assert.Contains("Reader", graded, StringComparison.Ordinal);
        Assert.Contains("Silver", graded, StringComparison.Ordinal);
        Assert.Equal("First Page", plain);
    }

    /// <summary>
    /// The series title comes from the reader, never from the stored row, so a notification follows
    /// whatever title language that reader prefers.
    /// </summary>
    [Fact]
    public void The_series_title_is_the_one_the_caller_supplies()
    {
        var (_, body) = Render("inbox.chapter.downloaded", new { chapter = "3" }, seriesTitle: "ベルセルク");

        Assert.Contains("ベルセルク", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_no_key_serves_its_stored_text()
    {
        var (title, body) = Renderer.Render(
            SupportedLanguages.Default, null, null, "Health issue", "A source is failing", null);

        Assert.Equal("Health issue", title);
        Assert.Equal("A source is failing", body);
    }

    private static (string Title, string Body) Render(
        string key, object args, string? seriesTitle = "Berserk") =>
        Renderer.Render(
            SupportedLanguages.Default,
            key,
            InboxRenderer.Serialize(Maki.Core.Inbox.InboxMessage.Args(args)),
            string.Empty,
            string.Empty,
            seriesTitle);

    private sealed class FixedLocale(string locale) : IRequestLocale
    {
        public string Locale { get; } = locale;
    }
}
