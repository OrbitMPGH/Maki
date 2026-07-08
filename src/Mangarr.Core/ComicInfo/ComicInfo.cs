using System.Xml.Serialization;

namespace Mangarr.Core.ComicInfo;

/// <summary>
/// The anansi-project ComicInfo.xml schema (v2.0), limited to the fields Kavita reads.
/// Serialized into the root of every CBZ.
/// </summary>
[XmlRoot("ComicInfo")]
public class ComicInfo
{
    public string? Title { get; set; }
    public string? Series { get; set; }
    public string? Number { get; set; }

    [XmlElement("Volume")]
    public string? VolumeSerialized { get; set; }

    [XmlElement("Count")]
    public string? CountSerialized { get; set; }

    public string? Summary { get; set; }
    public string? Year { get; set; }
    public string? Month { get; set; }
    public string? Day { get; set; }
    public string? Writer { get; set; }
    public string? Penciller { get; set; }
    public string? Genre { get; set; }
    public string? Tags { get; set; }
    public string? Web { get; set; }
    public string? LanguageISO { get; set; }
    public string? Manga { get; set; }
    public string? AgeRating { get; set; }
    public string? PageCount { get; set; }
}
