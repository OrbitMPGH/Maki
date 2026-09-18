using Maki.Api.Localization;
using Maki.Core.Configuration;
using Maki.Core.Security;

namespace Maki.Api.Tests;

/// <summary>
/// The order the request language is resolved in.
/// <para>
/// The rule worth protecting is that a stored preference outranks <c>Accept-Language</c>. The first
/// cut of this collapsed both into one "what the request asked for" value, and because browsers
/// attach Accept-Language to every single request, a user whose browser is in English never saw the
/// Swedish they had chosen. That reads as the setting being ignored, and nothing about it is visible
/// in a build.
/// </para>
/// </summary>
public class RequestLocaleTests
{
    [Fact]
    public void AnExplicitChoiceWinsOverEverything()
    {
        var locale = Build(chosen: "de", negotiated: "sv", stored: "sv", signedIn: true);
        Assert.Equal("de", locale.Locale);
    }

    [Fact]
    public void AStoredPreferenceBeatsAcceptLanguage()
    {
        // The browser says English on every request; the user chose Swedish once. Swedish wins.
        var locale = Build(chosen: null, negotiated: "en", stored: "sv", signedIn: true);
        Assert.Equal("sv", locale.Locale);
    }

    [Fact]
    public void AcceptLanguageIsUsedWhenNothingIsStored()
    {
        // The OPDS and curl path: a real client with no preference of ours to read.
        var locale = Build(chosen: null, negotiated: "sv", stored: null, signedIn: true);
        Assert.Equal("sv", locale.Locale);
    }

    [Fact]
    public void AnAnonymousRequestNeverReadsASetting()
    {
        var settings = new ThrowingUserSettings();
        var locale = new RequestLocaleContext(
            new TestCurrentUser(0, authenticated: false), settings, new TestUserLocaleResolver());
        locale.SetRequested(chosen: null, negotiated: "sv");

        Assert.Equal("sv", locale.Locale);
        Assert.False(settings.WasRead, "An unauthenticated request has no settings row to read.");
    }

    [Fact]
    public void FallsBackToTheInstanceDefault()
    {
        var locale = Build(chosen: null, negotiated: null, stored: null, signedIn: true);
        Assert.Equal("en", locale.Locale);
    }

    [Fact]
    public void AnUnsupportedStoredValueIsIgnoredRatherThanServed()
    {
        // A row written by a build that shipped a catalogue this one does not.
        var locale = Build(chosen: null, negotiated: "sv", stored: "klingon", signedIn: true);
        Assert.Equal("sv", locale.Locale);
    }

    [Fact]
    public void ResolvesOnceAndRemembers()
    {
        var settings = new FakeUserSettings("sv");
        var locale = new RequestLocaleContext(
            new TestCurrentUser(1), settings, new TestUserLocaleResolver());
        locale.SetRequested(chosen: null, negotiated: null);

        _ = locale.Locale;
        _ = locale.Locale;

        Assert.Equal(1, settings.Reads);
    }

    private static RequestLocaleContext Build(string? chosen, string? negotiated, string? stored, bool signedIn)
    {
        var locale = new RequestLocaleContext(
            new TestCurrentUser(signedIn ? 1 : 0, authenticated: signedIn),
            new FakeUserSettings(stored),
            new TestUserLocaleResolver());
        locale.SetRequested(chosen, negotiated);
        return locale;
    }

    private sealed class FakeUserSettings(string? language) : IUserSettings
    {
        public int Reads { get; private set; }
        public int UserId => 1;

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            if (key == SettingKeys.UiLanguage) Reads++;
            return Task.FromResult(key == SettingKeys.UiLanguage ? language : null);
        }

        public Task<Dictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingUserSettings : IUserSettings
    {
        public bool WasRead { get; private set; }
        public int UserId => 0;

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            WasRead = true;
            return Task.FromResult<string?>(null);
        }

        public Task<Dictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
