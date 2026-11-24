using Maki.Sources.Common;
using Maki.Sources.MangaFire;
using PuppeteerSharp;
using Xunit.Abstractions;

namespace Maki.Sources.Tests.Browser;

/// <summary>
/// Starts the real headless shell once for the class. A machine without it installed (CI never
/// downloads it) runs these as no-ops and says so in the test output.
/// </summary>
public sealed class HeadlessShellFixture : IAsyncLifetime
{
    private readonly HeadlessBrowserLauncher _launcher = new();
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public bool Available => _context is not null;

    public async Task InitializeAsync()
    {
        if (BrowserInstaller.ExecutablePath() is not { } exe || !File.Exists(exe))
        {
            return;
        }

        // A rule with spaces, as MangaFire's resolver setting passes one: it has to reach Chromium
        // as one argument or the shell exits at launch.
        _browser = await _launcher.LaunchAsync(["--host-resolver-rules=MAP fixture.invalid 127.0.0.1"]);
        _context = await _browser.CreateBrowserContextAsync();
    }

    public async Task<IPage> PageAsync(string body)
    {
        var page = await _context!.NewPageAsync();
        await page.SetContentAsync($"<!doctype html><html><body>{body}</body></html>");
        return page;
    }

    public async Task DisposeAsync() => await _launcher.CloseAsync(_browser);
}

public class MangaFireDomScriptTests(HeadlessShellFixture shell, ITestOutputHelper output) : IClassFixture<HeadlessShellFixture>
{
    private const string Click = "onclick=\"window.clicked = this.id\"";

    private async Task<string?> ClickPagerAsync(string body, int pageNo = 3)
    {
        await using var page = await shell.PageAsync(body);
        var clicked = await page.EvaluateFunctionAsync<bool>(MangaFireBrowser.ClickPagerScript, pageNo);
        var id = await page.EvaluateExpressionAsync<string?>("window.clicked ?? null");
        Assert.Equal(clicked, id is not null);
        return id;
    }

    private bool Skip()
    {
        if (shell.Available)
        {
            return false;
        }

        output.WriteLine("The headless shell is not installed here; nothing was checked.");
        return true;
    }

    [Fact]
    public async Task The_arrow_by_class_comes_first()
    {
        if (Skip()) return;

        Assert.Equal("arrow", await ClickPagerAsync(
            $"<button id=named aria-label='Next page' {Click}>x</button>" +
            $"<div class=chapter-pager><button id=three {Click}>3</button><button id=arrow aria-label='Next page' {Click}>›</button></div>"));
    }

    [Fact]
    public async Task A_hidden_arrow_by_class_is_given_up_on_and_the_role_match_takes_the_next_visible_one()
    {
        if (Skip()) return;

        Assert.Equal("second-arrow", await ClickPagerAsync(
            $"<div class=pager><button id=hidden-arrow aria-label='Next page' style='visibility:hidden' {Click}>›</button>" +
            $"<button id=second-arrow aria-label='Next page' {Click}>›</button></div>" +
            $"<button id=named aria-label='Go to the NEXT PAGE' {Click}>›</button>"));
    }

    [Fact]
    public async Task The_role_candidate_skips_buttons_hidden_from_the_accessibility_tree()
    {
        if (Skip()) return;

        Assert.Equal("visible", await ClickPagerAsync(
            $"<button id=display-none hidden aria-label='Next page' {Click}>›</button>" +
            $"<div aria-hidden=true><button id=aria-hidden aria-label='Next page' {Click}>›</button></div>" +
            $"<button id=visible aria-label='next page' {Click}>›</button>"));
    }

    [Fact]
    public async Task The_accessible_name_reads_labelledby_then_label_then_text_before_title()
    {
        if (Skip()) return;

        Assert.Equal("labelled", await ClickPagerAsync(
            $"<button id=titled title='Next page' {Click}>Go</button>" +
            $"<span id=caption>Next page</span><button id=labelled aria-labelledby=caption {Click}>›</button>"));
        Assert.Equal("title-only", await ClickPagerAsync(
            $"<button id=titled title='Next page' {Click}>Go</button>" +
            $"<button id=title-only title='Next page' {Click}></button>" +
            "<style>#title-only { width: 20px; height: 20px; }</style>"));
    }

    [Fact]
    public async Task Disabled_means_the_control_a_disabled_fieldset_or_aria_disabled_on_an_ancestor()
    {
        if (Skip()) return;

        Assert.Equal("three", await ClickPagerAsync(
            $"<button id=native disabled aria-label='Next page' {Click}>›</button>" +
            $"<div class=pager><button id=three {Click}>3</button></div>"));
        Assert.Equal("three", await ClickPagerAsync(
            $"<fieldset disabled><button id=fieldset aria-label='Next page' {Click}>›</button></fieldset>" +
            $"<div class=pager><button id=three {Click}>3</button></div>"));
        Assert.Equal("three", await ClickPagerAsync(
            $"<div aria-disabled=true><button id=ancestor aria-label='Next page' {Click}>›</button></div>" +
            $"<div class=pager><button id=three {Click}>3</button></div>"));
        Assert.Equal("legend", await ClickPagerAsync(
            $"<fieldset disabled><legend><button id=legend aria-label='Next page' {Click}>›</button></legend></fieldset>"));
    }

    [Fact]
    public async Task The_numbered_button_has_to_read_exactly_the_page()
    {
        if (Skip()) return;

        Assert.Equal("three", await ClickPagerAsync(
            $"<div class=pager><button id=thirty {Click}>30</button><button id=three {Click}> 3 </button></div>"));
        Assert.Null(await ClickPagerAsync($"<div class=pager><button id=four {Click}>4</button></div>"));
    }

    [Fact]
    public async Task The_language_menu_opens_from_the_select_naming_lang_and_picks_the_exact_label()
    {
        if (Skip()) return;

        await using var page = await shell.PageAsync(
            $"<button class=select id=sort {Click}>Sort</button><button class=select id=lang {Click}>🌐 Lang: EN</button>" +
            "<div class=dropdown__menu>" +
            "<button id=pt onclick=\"window.picked = this.id\">🇵🇹 Portuguese</button>" +
            "<button id=br onclick=\"window.picked = this.id\">🇧🇷 Portuguese (Br)</button></div>");

        Assert.True(await page.EvaluateFunctionAsync<bool>(MangaFireBrowser.OpenLanguageMenuScript));
        Assert.Equal("lang", await page.EvaluateExpressionAsync<string>("window.clicked"));
        Assert.True(await page.EvaluateFunctionAsync<bool>(MangaFireBrowser.ClickMenuItemScript, "Portuguese (Br)"));
        Assert.Equal("br", await page.EvaluateExpressionAsync<string>("window.picked"));
        Assert.False(await page.EvaluateFunctionAsync<bool>(MangaFireBrowser.ClickMenuItemScript, "Japanese"));
    }
}
