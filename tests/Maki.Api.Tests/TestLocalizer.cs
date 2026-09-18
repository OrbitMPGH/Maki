using Maki.Api.Localization;

namespace Maki.Api.Tests;

/// <summary>
/// An <see cref="ILocalizer"/> for tests that construct a controller by hand.
/// <para>
/// Answers the key itself rather than a message, because that is what a test wants to assert on:
/// the interesting question is which failure a controller reported, not how it was worded, and a
/// key survives a copy edit where an English sentence does not. The real catalogue is checked
/// separately by <see cref="LocalizationCatalogTests"/>.
/// </para>
/// <para>
/// Arguments are appended so a test can still tell two calls on the same key apart.
/// </para>
/// </summary>
public sealed class TestLocalizer : ILocalizer, IMessageCatalog
{
    public string Get(string key, object? args = null) => Render(key, args);

    public string GetFor(string locale, string key, object? args = null) => $"{locale}:{Render(key, args)}";

    private static string Render(string key, object? args)
    {
        if (args is null) return key;

        var parts = args.GetType().GetProperties()
            .Select(p => $"{p.Name}={p.GetValue(args)}")
            .ToArray();
        return parts.Length == 0 ? key : $"{key}({string.Join(", ", parts)})";
    }
}

/// <summary>
/// An <see cref="IUserLocaleResolver"/> for the same tests. Everyone reads English and nothing is
/// cached, which is all a controller test needs.
/// </summary>
public sealed class TestUserLocaleResolver : IUserLocaleResolver
{
    public Task<string> ResolveAsync(int userId, CancellationToken ct = default) => Task.FromResult("en");

    public Task<string> DefaultAsync(CancellationToken ct = default) => Task.FromResult("en");

    public void Forget(int userId) { }
}

/// <summary>
/// An <see cref="IRequestLocale"/> for tests. English, because <see cref="TestLocalizer"/> answers
/// the key regardless and the interesting assertions are about which message was chosen, not which
/// language it came out in.
/// </summary>
public sealed class TestRequestLocale : IRequestLocale
{
    public string Locale => "en";
}
