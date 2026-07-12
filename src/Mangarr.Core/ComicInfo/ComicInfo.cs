using System.Xml.Serialization;

namespace Mangarr.Core.ComicInfo;

/// <summary>
/// The anansi-project ComicInfo.xml schema (v2.0), limited to the fields Kavita reads.
/// Serialized into the root of every CBZ. Fields Mangarr never writes itself are still
/// declared so that rewriting an imported file's ComicInfo.xml round-trips them.
/// </summary>
[XmlRoot("ComicInfo")]
public class ComicInfo
{
    public string? Title { get; set; }
    public string? Series { get; set; }
    public string? LocalizedSeries { get; set; }
    public string? Number { get; set; }

    [XmlElement("Volume")]
    public string? VolumeSerialized { get; set; }

    [XmlElement("Count")]
    public string? CountSerialized { get; set; }

    public string? AlternateSeries { get; set; }
    public string? AlternateNumber { get; set; }
    public string? AlternateCount { get; set; }
    public string? StoryArc { get; set; }
    public string? StoryArcNumber { get; set; }
    public string? SeriesGroup { get; set; }
    public string? Summary { get; set; }
    public string? Notes { get; set; }
    public string? Year { get; set; }
    public string? Month { get; set; }
    public string? Day { get; set; }
    public string? Writer { get; set; }
    public string? Penciller { get; set; }
    public string? Inker { get; set; }
    public string? Colorist { get; set; }
    public string? Letterer { get; set; }
    public string? CoverArtist { get; set; }
    public string? Editor { get; set; }
    public string? Translator { get; set; }
    public string? Publisher { get; set; }
    public string? Imprint { get; set; }
    public string? Genre { get; set; }
    public string? Tags { get; set; }
    public string? Web { get; set; }
    public string? Format { get; set; }
    public string? LanguageISO { get; set; }
    public string? Manga { get; set; }
    public string? AgeRating { get; set; }
    public string? ScanInformation { get; set; }
    public string? GTIN { get; set; }
    public string? PageCount { get; set; }
}
