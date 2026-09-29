namespace Maki.Core.Quality;

/// <summary>What a <see cref="Entities.FormatCondition"/> tests, and how its <c>Value</c> is read.</summary>
public enum FormatConditionType
{
    /// <summary>Comma separated source names, case-insensitive.</summary>
    SourceIs,

    /// <summary>Comma separated <see cref="Sources.SourceKind"/> names.</summary>
    SourceKindIs,

    /// <summary>Regex, case-insensitive.</summary>
    GroupMatches,

    /// <summary>Regex, case-insensitive.</summary>
    ReleaseNameMatches,

    /// <summary>Pixels; matches when the median page width is at least this.</summary>
    MinWidth,

    /// <summary>Comma separated of jpg, png, webp, avif, mixed.</summary>
    ImageFormatIs,

    /// <summary>Matches when size divided by page count is at least this.</summary>
    MinBytesPerPage,

    /// <summary>Matches when the page count is at least this.</summary>
    MinPages,

    /// <summary>Comma separated language codes.</summary>
    LanguageIs
}
