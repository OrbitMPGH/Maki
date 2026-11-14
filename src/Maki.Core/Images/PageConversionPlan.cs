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

/// <param name="Stop">The stop a matching file's black-and-white pages are converted at; unused when <paramref name="Skip"/> is true.</param>
/// <param name="Format">The format of its black-and-white pages; null takes the plan's.</param>
/// <param name="Colour">How its colour pages are converted; null converts them like its black-and-white pages.</param>
public sealed record PlanRule(int Order, string Name, PageConversionConditions Conditions, bool Skip, PageConversionStop? Stop,
    PageConversionFormat? Format = null, PageTarget? Colour = null);

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

    /// <param name="Reason">"override", "rule", "alreadycompact" or "converted".</param>
    public sealed record Skip(string Reason) : PlanAction;

    /// <param name="Source">"override", "rule", "default", or "split" for a file only its tall pages are cut in.</param>
    public sealed record Convert(PageConversionSettings Settings, string Source) : PlanAction;

    /// <summary>What <c>ChapterFile.CheckedWith</c> records: "skip" or the settings name.</summary>
    public string Name => this is Convert convert ? convert.Settings.Name() : SkipName;
}

/// <summary>
/// The one answer to "what happens to this file", for the library pass, the download path and the
/// wizard. The series override wins, then a file already stored as WebP or AVIF is left alone (or only
/// has its tall pages split), then the first matching rule, then the default stop. Hard skips that
/// need the disk or other rows (hardlinked, PDF, a download in flight, read-file cleanup due) are the caller's.
/// </summary>
/// <param name="Format">The default format of black-and-white pages, and of colour pages while <paramref name="DefaultColour"/> is unset.</param>
/// <param name="SplitHeight">Strips taller than this are cut into pages (<see cref="TallPageSplitter"/>); null leaves them whole.</param>
/// <param name="DefaultColour">How colour pages are converted by default; null converts them like black-and-white pages.</param>
public sealed record PageConversionPlan(
    PageConversionFormat Format, PageConversionStop DefaultStop, bool PngLossless, IReadOnlyList<PlanRule> Rules,
    int? SplitHeight = null, PageTarget? DefaultColour = null)
{
    /// <summary>A plan with no rules: every file the series allows goes to these settings.</summary>
    public static PageConversionPlan For(PageConversionSettings settings) =>
        new(settings.Format, settings.Stop, settings.PngLossless, [], settings.SplitHeight, settings.Colour);

    /// <summary>For a file whose pages stay as they are and only its tall strips are cut.</summary>
    public PageConversionSettings SplitOnlySettings => new(
        Format, Lossy(DefaultStop), false, SplitHeight, SplitOnly: true,
        Colour: DefaultColour is { } colour ? colour with { Stop = Lossy(colour.Stop) } : null);

    private static PageConversionStop Lossy(PageConversionStop stop) =>
        stop == PageConversionStop.Lossless ? PageConversionStop.High : stop;

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
            return SplitHeight is null || MatchingRule(c) is { Skip: true }
                ? new PlanAction.Skip("alreadycompact")
                : new PlanAction.Convert(SplitOnlySettings, "split");
        }

        if (MatchingRule(c) is { } rule)
        {
            return rule.Skip
                ? new PlanAction.Skip("rule")
                : new PlanAction.Convert(Settings(rule), "rule");
        }

        return new PlanAction.Convert(DefaultSettings, "default");
    }

    /// <summary>
    /// For a file conversion already rewrote: its pages are never converted again, so the only thing left
    /// to do is cut its tall strips, when the plan splits and neither the series nor a rule says to skip it.
    /// </summary>
    public PlanAction ResolveConverted(PlanCandidate c)
    {
        if (c.SeriesOverride == PageConversionOverride.Never)
        {
            return new PlanAction.Skip("override");
        }

        return SplitHeight is null || MatchingRule(c) is { Skip: true }
            ? new PlanAction.Skip("converted")
            : new PlanAction.Convert(SplitOnlySettings, "split");
    }

    /// <summary>The first rule in order whose conditions match, whatever the override or format says.</summary>
    public PlanRule? MatchingRule(PlanCandidate c) =>
        Rules.OrderBy(r => r.Order).FirstOrDefault(r => r.Conditions.Matches(c));

    /// <summary>The default formats at one stop, for the default and for a series override.</summary>
    public PageConversionSettings Settings(PageConversionStop stop) =>
        Settings(new PageTarget(Format, stop), DefaultColour is { } colour ? colour with { Stop = stop } : null);

    /// <summary>What a file no rule matches is converted with.</summary>
    public PageConversionSettings DefaultSettings => Settings(new PageTarget(Format, DefaultStop), DefaultColour);

    public PageConversionSettings Settings(PlanRule rule) =>
        Settings(new PageTarget(rule.Format ?? Format, rule.Stop ?? DefaultStop), rule.Colour);

    /// <summary>
    /// A colour target equal to the black-and-white one is dropped, so the settings name stays the one a
    /// plan without colour targets had. PNG pages only follow <see cref="PngLossless"/> while a target is lossy.
    /// </summary>
    private PageConversionSettings Settings(PageTarget grey, PageTarget? colour)
    {
        grey = grey.Normalised();
        colour = colour?.Normalised();
        if (colour == grey)
        {
            colour = null;
        }

        var lossy = grey.Stop != PageConversionStop.Lossless || colour is { Stop: not PageConversionStop.Lossless };
        return new PageConversionSettings(grey.Format, grey.Stop, lossy && PngLossless, SplitHeight, Colour: colour);
    }

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
