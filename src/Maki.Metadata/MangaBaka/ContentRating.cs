namespace Maki.Metadata.MangaBaka;

/// <summary>
/// MangaBaka's <c>content_rating</c> vocabulary, ordered least to most explicit. Each user carries
/// a single ceiling rating (<c>MakiUser.MaxContentRating</c>); everything at or below it in this
/// order is shown to them.
/// <para>
/// The ceiling is a per-user value, never an instance setting: it used to live in
/// <c>discover.maxcontentrating</c>, which the <c>PerUserData</c> migration deletes. Callers pass
/// the current user's value in — there is deliberately no "read it from somewhere" helper here,
/// because the one that existed went on reading the deleted key and every user was filtered at
/// <see cref="Default"/> no matter what their account said.
/// </para>
/// </summary>
public static class ContentRating
{
    public const string Safe = "safe";
    public const string Suggestive = "suggestive";
    public const string Erotica = "erotica";
    public const string Pornographic = "pornographic";

    public static readonly string[] All = [Safe, Suggestive, Erotica, Pornographic];

    /// <summary>What an account gets when nothing better is known — excludes only Pornographic.</summary>
    public const string Default = Erotica;

    public static bool IsValid(string? rating) => rating is not null && Array.IndexOf(All, rating) >= 0;

    /// <summary>
    /// Ratings at or below <paramref name="max"/> in <see cref="All"/>'s order. An unknown or absent
    /// value falls back to <see cref="Safe"/>, not to <see cref="Default"/>: this is the ceiling a
    /// parental control rests on, so an unreadable one has to fail closed. It never returns an empty
    /// list, which would render as an empty SQL <c>IN ()</c>.
    /// </summary>
    public static IReadOnlyList<string> Allowed(string? max)
    {
        var index = max is null ? -1 : Array.IndexOf(All, max);
        return All.Take(index < 0 ? 1 : index + 1).ToList();
    }
}
