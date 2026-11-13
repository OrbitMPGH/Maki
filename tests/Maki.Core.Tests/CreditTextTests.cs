using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>Invented page texts, joined the way the OCR reader joins its lines.</summary>
public class CreditTextTests
{
    [Theory]
    [InlineData("MOONLIT PAGES | Join our Discord | discord.gg/example")]
    [InlineData("WE ARE | RECRUITING | typesetters and cleaners")]
    [InlineData("READAT | EXAMPLESCANS.COM | FOR FAST | RELEASES")]
    [InlineData("SUPPORT THE | AUTHOR BY BUYING | THE BOOKS")]
    [InlineData("Please do not repost our work")]
    [InlineData("Lantern Scans presents")]
    public void Strong_signs_make_a_scanlator_page(string text) => Assert.True(CreditText.IsScanlator(text, colour: false));

    [Theory]
    [InlineData("TL: Alder | PR: Birch | TS: Cedar")]
    [InlineData("Proofreader Birch")]
    [InlineData("raw provider: someone")]
    public void Weak_signs_count_on_a_page_without_publisher_markers(string text) =>
        Assert.True(CreditText.IsScanlator(text, colour: false));

    [Fact]
    public void A_colophon_listing_its_translator_and_proofreader_is_a_publishers()
    {
        const string colophon = "Story and art by Example Author | Translation: Jane Roe | Proofreader: Sam Poe | "
            + "(c) 2020 Example Author. All rights reserved. | ISBN 978-0-00-000000-0 | www.example.com";

        Assert.False(CreditText.IsScanlator(colophon, colour: true));
    }

    [Fact]
    public void A_strong_sign_beside_two_publisher_markers_is_a_publishers()
    {
        Assert.False(CreditText.IsScanlator("Join our newsletter | Published by Example Media | All rights reserved", colour: true));
        Assert.True(CreditText.IsScanlator("Join our Discord | (c) Example Author, all rights reserved to the author", colour: false));
    }

    [Fact]
    public void A_localized_by_card_is_a_publishers()
    {
        Assert.False(CreditText.IsScanlator("Localized by | Example Studio", colour: true));
        Assert.False(CreditText.IsScanlator("Authorized for Publishing on Example Platform", colour: true));
    }

    [Fact]
    public void An_official_title_page_with_its_staff_list_is_a_publishers()
    {
        const string title = "LANTERN | HUNTER | ORIGINAL NOVEL BY + EXAMPLE AUTHOR | ART BY + EXAMPLE ARTIST | "
            + "TRANSLATION BY + JANE ROE | TYPESETTING BY + SAM POE | PRIMARY QC BY + ALEX DOE";

        Assert.False(CreditText.IsScanlator(title, colour: true));
        Assert.True(CreditText.IsScanlator("Original novel by Example Author | Join our Discord", colour: false));
    }

    [Fact]
    public void A_bare_web_address_counts_only_with_the_colour_rule_behind_it()
    {
        Assert.True(CreditText.IsScanlator("visit example.com", colour: true));
        Assert.False(CreditText.IsScanlator("visit example.com", colour: false));
    }

    [Fact]
    public void A_page_without_text_or_signs_is_not_a_scanlators()
    {
        Assert.False(CreditText.IsScanlator("", colour: true));
        Assert.False(CreditText.IsScanlator("Chapter 12 | The Lantern Festival", colour: true));
    }
}
