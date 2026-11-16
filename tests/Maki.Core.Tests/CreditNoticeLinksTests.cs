using Maki.Core.Images;

namespace Maki.Core.Tests;

public class CreditNoticeLinksTests
{
    private static readonly string[] Title = ["Lanterns Over Kasumi"];

    /// <summary>Unrelated notices, so pieces every group writes ("discord", "translator") weigh little.</summary>
    private static IEnumerable<CreditLinkText> Background(int from) =>
    [
        new(from, "MOONLIT SCANS | Translator: Hana | Join us on Discord | Read it first on our site", Title),
        new(from + 1, "Kitsune Translations | Support us on Ko-fi | Translator: Ren", Title),
        new(from + 2, "PAPER CRANE | TL: Mo | PR: Jun | Please do not repost | discord.gg/crane", Title),
        new(from + 3, "We are recruiting typesetters and cleaners | Join our discord", Title),
        new(from + 4, "Hollow Lantern | Thanks for reading | See you next week", Title),
    ];

    // Three end cards of one group: different art and staff each chapter, the same sign-off, handle and border.
    private static readonly CreditLinkText[] Whims =
    [
        new(1, "onawhimonawhimonawhimonawhim | tl: scrubb | CL/RD: lolibacon / ts: occu | Have a Good One. | CURRNT", Title),
        new(2, "ineedaJPTLfortheworksonmylist.pleasereachout,luvu. | hmu@occu#3161 | Have a Good One. | onawhimonawhim", Title),
        new(3, "Credits to LATAM TRANSLATION | entl&ts:occu | thanksforreadin' | onawhimonawhimonawhim | Have a Good One.", Title),
    ];

    [Fact]
    public void Notices_sharing_a_sign_off_and_handles_link_and_unrelated_ones_do_not()
    {
        var s = CreditNoticeLinks.Measure([.. Whims, .. Background(10)]);

        Assert.True(s.Of(1, 2) >= CreditNoticeLinks.LinkAt, $"1~2 {s.Of(1, 2)}");
        Assert.True(s.Of(1, 3) >= CreditNoticeLinks.LinkAt, $"1~3 {s.Of(1, 3)}");
        Assert.All(Enumerable.Range(10, 5), id => Assert.True(s.Of(1, id) < CreditNoticeLinks.LinkAt, $"1~{id} {s.Of(1, id)}"));
        Assert.Equal(s.Of(1, 2), s.Of(2, 1));
    }

    [Fact]
    public void Official_staff_lines_and_the_series_title_link_nothing()
    {
        string[] title = ["Dungeon Crawler Carl"];
        var s = CreditNoticeLinks.Measure([
            new(1, "DUNGEON CRAWLER CARL | original story by MATT DINNIMAN | lead art: KUZOMARI | Kayn scan | dsc.gg/kayn", title),
            new(2, "DungeonCrawlerCarl | original story by MATT DINNIMAN | lead art: KUZOMARI | ELF TOON | elftoon.com", title),
            .. Background(10),
        ]);

        Assert.Equal(0, s.Of(1, 2));
    }

    [Fact]
    public void A_piece_only_one_notice_has_or_every_notice_has_links_nothing()
    {
        var s = CreditNoticeLinks.Measure([new(1, "Thanks for reading", Title), new(2, "Thanks for reading", Title), new(3, "", Title)]);

        Assert.Empty(s.Pairs());
    }

    [Fact]
    public void Unnamed_notices_join_the_group_they_read_like()
    {
        var s = CreditNoticeLinks.Measure([.. Whims, .. Background(10)]);
        CreditLinkNode[] nodes = [new([1], "currentwhims", true), new([2], null, false), new([3], null, false), new([10], "moonlitscans", true), new([11], null, false)];

        var sets = CreditNoticeLinks.Join(nodes, s);

        Assert.Contains(sets, set => set.SequenceEqual([0, 1, 2]));
        Assert.Contains(sets, set => set.SequenceEqual([3]));
        Assert.Contains(sets, set => set.SequenceEqual([4]));
    }

    [Fact]
    public void Two_names_the_user_set_never_join_however_alike()
    {
        var s = CreditNoticeLinks.Measure([.. Whims, .. Background(10)]);

        var sets = CreditNoticeLinks.Join([new([1], "currentwhims", true), new([2], "latamtranslation", true), new([3], null, false)], s);

        Assert.DoesNotContain(sets, set => set.Contains(0) && set.Contains(1));
        Assert.Equal(2, sets.Count);
    }

    [Fact]
    public void One_strong_pair_does_not_pull_a_notice_into_a_bucket_it_reads_nothing_like()
    {
        // Notice 4 shares one card with notice 1 and nothing with the nine others 1 is listed with.
        CreditLinkText[] others = [.. Enumerable.Range(20, 9).Select(i => new CreditLinkText(i, $"Crew number {i} | staff {i}{i}{i}", Title))];
        var s = CreditNoticeLinks.Measure([
            .. Whims,
            new(4, "onawhimonawhimonawhimonawhim | tl: scrubb | CL/RD: lolibacon / ts: occu | Have a Good One. | CURRNT | ELF TOON | elftoon.com", Title),
            .. others,
            .. Background(10),
        ]);
        Assert.True(s.Of(1, 4) >= CreditNoticeLinks.LinkAt);

        var together = CreditNoticeLinks.Join([new([1, 2, 3], null, false), new([4], null, false)], s);
        var apart = CreditNoticeLinks.Join([new([1, .. others.Select(o => o.Id)], null, false), new([4], null, false)], s);

        Assert.Single(together);
        Assert.Equal(2, apart.Count);
    }
}
