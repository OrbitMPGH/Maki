namespace Maki.Core.Quality;

/// <summary>
/// Which volume release proposals Maki may approve on its own, for an owner who would rather not
/// answer them. Only a digital release, within a hard size ceiling, whose only reasons for waiting are
/// the ones that cost nothing worth a look: it names no span, it adds chapters, or it is over the
/// auto-grab size. Anything that says Maki is unsure what the release is (unknown chapters, a volume
/// span it cannot map, a trailing number that may belong to the title) still waits.
/// </summary>
public static class ProposalAutoApproval
{
    /// <summary>About 50 volumes at the 250 MB a digital volume takes.</summary>
    public const long DefaultMaxBytes = 12L * 1024 * 1024 * 1024;

    public const long MinMaxBytes = 100L * 1024 * 1024;
    public const long MaxMaxBytes = 200L * 1024 * 1024 * 1024;

    public static readonly IReadOnlyList<string> AllowedReasons =
    [
        SpanVerdictReasons.WholeSeriesPack,
        SpanVerdictReasons.AddsMissingChapters,
        SpanVerdictReasons.OverBudget
    ];

    public static long ClampMaxBytes(long value) => Math.Clamp(value, MinMaxBytes, MaxMaxBytes);

    public static bool Allows(IReadOnlyCollection<string> reasons, long sizeBytes, bool isDigital, long maxBytes) =>
        isDigital && sizeBytes > 0 && sizeBytes <= maxBytes && reasons.Count > 0 && reasons.All(AllowedReasons.Contains);
}
