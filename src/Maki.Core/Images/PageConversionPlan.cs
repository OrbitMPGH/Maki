using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Entities;
using Maki.Core.Quality;

namespace Maki.Core.Images;

/// <summary>What the plan knows about one file, read from its row or, for a download, from the queue item and the archive.</summary>
public sealed record PlanCandidate(
    PageConversionOverride SeriesOverride,
    string? SeriesType,
    IReadOnlyList<string> SeriesTags,
    QualityTier? Tier,
    string? SourceName,
    string? ImageFormat,
    long Size,
    int? PageCount,
    int? MedianWidth,
    int? MedianHeight)
{
    /// <summary>
    /// Raw bits per pixel, unlike <c>MeasuredQuality.BitsPerPixel</c> which discounts PNG for upgrade
    /// scoring. Null for a file that was never measured.
    /// </summary>
    public double? BitsPerPixel =>
        PageCount is > 0 && MedianWidth is > 0 && MedianHeight is > 0
            ? Size * 8d / PageCount.Value / ((double)MedianWidth.Value * MedianHeight.Value)
            : null;
}

/// <summary>
/// What a rule matches on. Every field that is set has to match; an unset one matches anything, so
/// an empty set of conditions matches every file. A file with no format or no bits per pixel does
/// not match a condition on them.
/// </summary>
/// <param name="Tiers">Lowercase <see cref="QualityTier"/> names.</param>
/// <param name="Sources">Source names, case-insensitive; a trailing * matches any rest ("torrent:*").</param>
/// <param name="Formats">Page format names as measured: jpg, png, mixed and so on.</param>
/// <param name="Tags">Matches when the series carries any of these user tags.</param>
public sealed record PageConversionConditions(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Tiers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Sources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Formats = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? SeriesTypes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Tags = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BppUnder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BppOver = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SizeOverBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SizeUnderBytes = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Throws <see cref="JsonException"/> on anything that is not a JSON object of this shape.</summary>
    public static PageConversionConditions Parse(string json) =>
        (JsonSerializer.Deserialize<PageConversionConditions>(json, Json) ?? throw new JsonException("The conditions are null"))
        .Normalised();

    public static bool TryParse(string? json, out PageConversionConditions conditions)
    {
        conditions = new PageConversionConditions();
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            conditions = Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Normalised(), Json);

    /// <summary>Blank entries dropped, and a list left empty is no condition at all.</summary>
    public PageConversionConditions Normalised() => this with
    {
        Tiers = Clean(Tiers, lower: true),
        Sources = Clean(Sources, lower: false),
        Formats = Clean(Formats, lower: true),
        SeriesTypes = Clean(SeriesTypes, lower: false),
        Tags = Clean(Tags, lower: false)
    };

    [JsonIgnore]
    public bool IsEmpty => Normalised() is
    {
        Tiers: null, Sources: null, Formats: null, SeriesTypes: null, Tags: null,
        BppUnder: null, BppOver: null, SizeOverBytes: null, SizeUnderBytes: null
    };

    public bool Matches(PlanCandidate c)
    {
        if (Tiers is not null && (c.Tier is not { } tier || !Tiers.Contains(QualitySnapshot.TierName(tier), StringComparer.OrdinalIgnoreCase)))
            return false;
        if (Sources is not null && (c.SourceName is not { } source || !Sources.Any(p => SourceMatches(p, source))))
            return false;
        if (Formats is not null && (c.ImageFormat is not { } format || !Formats.Contains(format, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (SeriesTypes is not null && (c.SeriesType is not { } type || !SeriesTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (Tags is not null && !c.SeriesTags.Any(t => Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            return false;

        var bpp = c.BitsPerPixel;
        if (BppUnder is { } under && !(bpp < under))
            return false;
        if (BppOver is { } over && !(bpp > over))
            return false;
        if (SizeOverBytes is { } big && c.Size <= big)
            return false;
        if (SizeUnderBytes is { } small && c.Size >= small)
            return false;
        return true;
    }

    private static bool SourceMatches(string pattern, string source) =>
        pattern.EndsWith('*')
            ? source.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, source, StringComparison.OrdinalIgnoreCase);

    private static string[]? Clean(IReadOnlyList<string>? values, bool lower)
    {
        var cleaned = values?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => lower ? v.Trim().ToLowerInvariant() : v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return cleaned is { Length: > 0 } ? cleaned : null;
    }
}

/// <param name="Stop">The stop a matching file is converted at; unused when <paramref name="Skip"/> is true.</param>
public sealed record PlanRule(int Order, string Name, PageConversionConditions Conditions, bool Skip, PageConversionStop? Stop);

/// <summary>What happens to one file: left as it is, or converted with these settings.</summary>
public abstract record PlanAction
{
    public const string SkipName = "skip";

    /// <summary>
    /// What the pass records for a file it can never convert whatever the plan says (hardlinked, a PDF,
    /// a path that does not resolve), so a recheck leaves it checked instead of queueing it again.
    /// </summary>
    public const string HardSkipName = "skip:hard";

    /// <summary>
    /// What an undo records on a file it put the original back for. Like <see cref="HardSkipName"/>, the
    /// pass and rechecks leave it alone until new bytes reset the row.
    /// </summary>
    public const string UndoneName = "undone";

    /// <param name="Reason">"override", "rule" or "alreadycompact".</param>
    public sealed record Skip(string Reason) : PlanAction;

    /// <param name="Source">"override", "rule" or "default".</param>
    public sealed record Convert(PageConversionSettings Settings, string Source) : PlanAction;

    /// <summary>What <c>ChapterFile.CheckedWith</c> records: "skip" or the settings name.</summary>
    public string Name => this is Convert convert ? convert.Settings.Name() : SkipName;
}

/// <summary>
/// The one answer to "what happens to this file", for the library pass, the download path and the
/// wizard. The series override wins, then a file already stored as WebP or AVIF is left alone, then
/// the first matching rule, then the default stop. Hard skips that need the disk or other rows
/// (hardlinked, PDF, a download in flight, read-file cleanup due) are the caller's.
/// </summary>
public sealed record PageConversionPlan(
    PageConversionFormat Format, PageConversionStop DefaultStop, bool PngLossless, IReadOnlyList<PlanRule> Rules)
{
    /// <summary>A plan with no rules: every file the series allows goes to these settings.</summary>
    public static PageConversionPlan For(PageConversionSettings settings) =>
        new(settings.Format, settings.Stop, settings.PngLossless, []);

    public PlanAction Resolve(PlanCandidate c)
    {
        if (c.SeriesOverride == PageConversionOverride.Never)
        {
            return new PlanAction.Skip("override");
        }

        if (StopOf(c.SeriesOverride) is { } forced)
        {
            return new PlanAction.Convert(Settings(forced), "override");
        }

        if (IsAlreadyCompact(c.ImageFormat))
        {
            return new PlanAction.Skip("alreadycompact");
        }

        if (MatchingRule(c) is { } rule)
        {
            return rule.Skip
                ? new PlanAction.Skip("rule")
                : new PlanAction.Convert(Settings(rule.Stop ?? DefaultStop), "rule");
        }

        return new PlanAction.Convert(Settings(DefaultStop), "default");
    }

    /// <summary>The first rule in order whose conditions match, whatever the override or format says.</summary>
    public PlanRule? MatchingRule(PlanCandidate c) =>
        Rules.OrderBy(r => r.Order).FirstOrDefault(r => r.Conditions.Matches(c));

    public PageConversionSettings Settings(PageConversionStop stop) => stop == PageConversionStop.Lossless
        ? new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.Lossless, false)
        : new PageConversionSettings(Format, stop, PngLossless);

    public static bool IsAlreadyCompact(string? format) =>
        string.Equals(format, "webp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "avif", StringComparison.OrdinalIgnoreCase);

    public static PageConversionStop? StopOf(PageConversionOverride mode) => mode switch
    {
        PageConversionOverride.Smallest => PageConversionStop.Smallest,
        PageConversionOverride.Compact => PageConversionStop.Compact,
        PageConversionOverride.Balanced => PageConversionStop.Balanced,
        PageConversionOverride.High => PageConversionStop.High,
        PageConversionOverride.Lossless => PageConversionStop.Lossless,
        _ => null
    };
}
