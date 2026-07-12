namespace Mangarr.Core.Entities;

public class Chapter
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public Series? Series { get; set; }

    /// <summary>Parsed chapter number; supports decimals like 10.5. Null for one-shots.</summary>
    public decimal? Number { get; set; }

    /// <summary>The original, unparsed chapter identifier from the source. Always preserved.</summary>
    public string? NumberRaw { get; set; }

    /// <summary>Null when the source does not group chapters into volumes.</summary>
    public int? Volume { get; set; }

    public string? Title { get; set; }
    public bool IsOneShot { get; set; }

    /// <summary>BCP-47 language tag, e.g. "en".</summary>
    public string Language { get; set; } = "en";

    public DateTime? ReleaseDate { get; set; }
    public bool Monitored { get; set; } = true;

    public int? ChapterFileId { get; set; }
    public ChapterFile? ChapterFile { get; set; }

    /// <summary>A special is a decimal-numbered chapter (10.5 omake etc.); one-shots are not specials.</summary>
    public static bool IsSpecial(decimal? number) => number is { } n && n % 1 != 0;

    /// <summary>Whether a chapter with this number should be monitored under the given mode.</summary>
    public static bool MonitoredUnder(NewChapterMonitorMode mode, decimal? number) => mode switch
    {
        NewChapterMonitorMode.All => true,
        NewChapterMonitorMode.MainOnly => !IsSpecial(number),
        _ => false
    };
}
