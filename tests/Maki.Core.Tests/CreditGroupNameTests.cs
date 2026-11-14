using System.Text.RegularExpressions;
using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>
/// Group names read off real notice texts from the 2026-11-14 scan (git-ignored
/// <c>research/credit-pages/2026-11-14-efficiency/out/notices-6-2-rows.json</c>, notice ids in the names).
/// </summary>
public class CreditGroupNameTests
{
    private const string Asura663 =
        "THE DARK MAGE'S | RETURN TO ENLISTMENT | LEAF | SSG | AsuraScans | RUSH | RUSH | asura.gg/discord | 9 | SCARPET | QC | SCARPET | as ura.gg";

    private const string Omega487 =
        "QMEGA SCANS | 2 | WIRELESS ONAHOLE | Stafr | Stafr | PR | Stafr | Stafr | discord.gg/omegascans";

    private const string Omega488 = "QMEGASCANS | READEARLY ON OMEGASCANS.ORG";

    private const string Setsu186 = "HAIMIYA SENPAI IS SCARY | AND CUTE | SETSU SCANS | TL:ZN69V | RD:SATYAM | TS:ZAF | QC:SATYAM";

    private const string Skythewood204 =
        "Hokagori | SKYTHEwOOd TRANnSLATIOnS | TL/CL/RD/TS: PINGAS I PR: SUU | m discord.gg/t6wm6AQ  | B skthewood.blogspot.com | Chansuki | Tonaneko";

    private const string Skythewood205 =
        "okayori | SKYTHEWOOD TRANSLATIONS | TL/Cl/RD/TS: PINGaS I PR: SuU | ? discord.g/t6wm6AQ | B skythewood.blogspot.com | Sugigukil | Tonaneko";

    private const string Thunder76 =
        "!WARNING! | READERTHATDOESNOTSUPPORT | OURWEBSITEHASBEENDEIECTED! | PLEASE READ THiS SERiES ON UR WEBSiTE | EN-THUNDERSCANS.COM";

    private const string LeftField0 =
        "LEFT | KAKKOII ONNANOKO TO 12-CENTINO YAKUSOKU | FIELD | PRESENTED BY LEFT FIELD SCANS | SCANS | CREDITS | COME JOIN OUR DISCORD! | " +
        "Looking for Staff! | TL:OSISNIE | Discord.gg/Yp8EHSP9GR | CLRD: ?BOX | PR: AKASUGI, OSISNIE | TS:FROSTFIRESOUL";

    private const string Reptile =
        "Brought to you by | REPTILEn*!? | SCANS | We're Recruiting! | We're looking for a Typesetter for | this series. | " +
        "If interested, join our Discord, | oP contact Bhindi#5263!";

    private const string Bato438 =
        "DONOTREPOST | IF YOU ENJOYEDTHIS SERIES | ANDWANTAFASTERRELEASE | PLEASEREAD ON | BATO.TO | " +
        "DO SUPPORT THE AUTHOR BY PURCHASING THEIR WORKS | JOIN OUR DISCORD:https://bato.toldiscord";

    private const string Toon77 =
        "TooN. | .net | COMMIssIOn YOuR FAVORItE SERIEs | WE HAVE RICHT TO MAKE THE SERIES ON HOLD OR | NOW YOU CAN COMMISSION YOUR FAVORITE SERIES | " +
        "DISCONTINUE IT IF THE COMMISSIONERS FAIL TO PAY";

    [Theory]
    [InlineData(Asura663, "Asura Scans")]
    [InlineData(Setsu186, "SETSU SCANS")]
    [InlineData(Skythewood205, "SKYTHEWOOD TRANSLATIONS")]
    [InlineData(Thunder76, "EN-THUNDER SCANS")]
    [InlineData(LeftField0, "LEFT FIELD SCANS")]
    [InlineData(Reptile, "REPTILEn")]
    public void Names_the_group_a_real_notice_credits(string text, string expected) =>
        Assert.Equal(expected, CreditGroupName.Suggest([text]).Name);

    [Theory]
    [InlineData(Bato438)]
    [InlineData(Toon77)]
    [InlineData("Chapter 12 | The End?")]
    public void An_aggregator_ad_or_a_story_page_names_no_group(string text) =>
        Assert.Null(CreditGroupName.Suggest([text]).Name);

    [Fact]
    public void A_name_split_over_lines_is_joined() =>
        Assert.Equal("LEFT FIELD SCANS", CreditGroupName.Suggest(["LEFT | FIELD | SCANS"]).Name);

    [Fact]
    public void A_staff_label_on_its_own_line_does_not_pull_in_names() =>
        Assert.Null(CreditGroupName.Suggest(["KIRO | CLARA | TL"]).Name);

    [Fact]
    public void An_OCR_slip_keeps_its_spelling_until_a_confirmed_name_settles_it()
    {
        Assert.Equal("QMEGA SCANS", CreditGroupName.Suggest([Omega487]).Name);
        Assert.Equal("Omega Scans", CreditGroupName.Suggest([Omega487], known: ["Omega Scans"]).Name);
    }

    [Fact]
    public void Every_read_of_a_notice_votes_and_variants_merge()
    {
        // 204 alone reads a garbled title and a misspelt blog address, which is all it can offer.
        Assert.Equal("skthewood", CreditGroupName.Suggest([Skythewood204]).Name);
        // 488 adds "OMEGA SCANS" and "QMEGA SCANS" again; the variants merge into one name, and a repeated read counts once.
        Assert.Equal("QMEGA SCANS", CreditGroupName.Suggest([Omega487, Omega488, Omega487]).Name);
        Assert.Equal("Omega Scans", CreditGroupName.Suggest([Omega488, Omega487], known: ["Omega Scans"]).Name);
    }

    [Fact]
    public void The_discord_invite_is_kept_as_an_id()
    {
        Assert.Equal("Yp8EHSP9GR", CreditGroupName.Suggest([LeftField0]).Invite);
        Assert.Equal("omegascans", CreditGroupName.Suggest([Omega487]).Invite);
        Assert.Equal(CreditGroupGuess.None, CreditGroupName.Suggest([Bato438]));
    }

    [Fact]
    public void A_patreon_handle_names_the_group_but_the_service_does_not()
    {
        // The handle also reads as "<name> TL", which outscores it and keeps its own spelling.
        Assert.Equal("sleepy tl", CreditGroupName.Suggest(["Support us | patreon.com/sleepytl"]).Name);
        Assert.Equal("moonbeam", CreditGroupName.Suggest(["Support us | ko-fi.com/moonbeam"]).Name);
        Assert.Null(CreditGroupName.Suggest(["Support us | patreon.com"]).Name);
    }

    [Fact]
    public void A_pattern_timing_out_gives_no_suggestion_instead_of_throwing()
    {
        Assert.Equal(CreditGroupGuess.None,
            CreditGroupName.Safely<CreditGroupGuess>(() => throw new RegexMatchTimeoutException("x", "y", TimeSpan.Zero), CreditGroupGuess.None));
        Assert.Throws<InvalidOperationException>(() => CreditGroupName.Safely<int>(() => throw new InvalidOperationException(), 0));
    }

    [Fact]
    public void Blank_texts_count_for_nothing() =>
        Assert.Equal(CreditGroupGuess.None, CreditGroupName.Suggest([null, "", "  "]));
}
