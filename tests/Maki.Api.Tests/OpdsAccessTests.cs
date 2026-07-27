using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Opds;

namespace Maki.Api.Tests;

/// <summary>
/// The OPDS catalogue's authentication boundary and its one deviation from the built-in reader's
/// progress rule. Both used to live inline in the controller, where nothing could reach them.
/// </summary>
public sealed class OpdsAccessTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private void SetConfig(params (string Key, string Value)[] entries)
    {
        using var db = _db.NewContext();
        db.AppConfig.RemoveRange(db.AppConfig);
        db.AppConfig.AddRange(entries.Select(e => new AppConfigEntry { Key = e.Key, Value = e.Value }));
        db.SaveChanges();
    }

    private Task<OpdsAccess> ReadAsync() =>
        new OpdsAccessService(_db.NewContext()).ReadAsync(CancellationToken.None);

    // ---- the token check ----

    [Fact]
    public void ADisabledCatalogueAllowsNothingEvenWithTheRightToken()
    {
        // Answering "wrong token" rather than "correct token, but off" is the point: a disabled
        // catalogue must not confirm that it exists.
        Assert.False(new OpdsAccess(Enabled: false, "abc", TrackProgress: true).Allows("abc"));
    }

    [Fact]
    public void TheRightTokenOnAnEnabledCatalogueIsAllowed()
    {
        Assert.True(new OpdsAccess(Enabled: true, "abc", TrackProgress: true).Allows("abc"));
    }

    [Theory]
    [InlineData("ABC")] // case matters — the token is generated, never typed
    [InlineData("ab")]
    [InlineData("abcd")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRejected(string? provided)
    {
        Assert.False(new OpdsAccess(Enabled: true, "abc", TrackProgress: true).Allows(provided));
    }

    [Fact]
    public void ACatalogueWithNoTokenYetRejectsEvenAnEmptyRequest()
    {
        // Guards the state between "enabled" being written and a token existing: an empty stored
        // token must never match an empty supplied one.
        Assert.False(new OpdsAccess(Enabled: true, null, TrackProgress: true).Allows(null));
        Assert.False(new OpdsAccess(Enabled: true, "", TrackProgress: true).Allows(""));
    }

    // ---- reading the settings ----

    [Fact]
    public async Task AnUnconfiguredInstanceReadsAsOffWithTrackingOn()
    {
        SetConfig();

        var access = await ReadAsync();

        Assert.False(access.Enabled);
        Assert.Null(access.Token);
        // Absent means on; only an explicit "false" is the user having turned it off.
        Assert.True(access.TrackProgress);
    }

    [Fact]
    public async Task StoredSettingsAreReadBack()
    {
        SetConfig(
            (SettingKeys.OpdsEnabled, "true"),
            (SettingKeys.OpdsToken, "deadbeef"),
            (SettingKeys.OpdsTrackProgress, "false"));

        var access = await ReadAsync();

        Assert.True(access.Enabled);
        Assert.Equal("deadbeef", access.Token);
        Assert.False(access.TrackProgress);
        Assert.True(access.Allows("deadbeef"));
    }

    // ---- the last-page-first guard ----

    [Fact]
    public void FetchingTheLastPageOfAnUnopenedChapterDoesNotCompleteIt()
    {
        // Several readers fetch the final page up front to size their page bar. Completion is
        // sticky and fires a tracker event, so there would be nothing to undo.
        Assert.False(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 9, pageCount: 10));
    }

    [Fact]
    public void FetchingTheLastPageOfAnOpenedChapterFollowsTheNormalRule()
    {
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: true, page: 9, pageCount: 10));
    }

    [Fact]
    public void AnyEarlierPageFollowsTheNormalRule()
    {
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 0, pageCount: 10));
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 8, pageCount: 10));
    }

    [Fact]
    public void ASinglePageChapterIsAlsoCoveredByTheGuard()
    {
        // page 0 is both the first and the last page, so an unopened one-page chapter must not be
        // completed by the reader merely looking at it.
        Assert.False(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 0, pageCount: 1));
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: true, page: 0, pageCount: 1));
    }
}
