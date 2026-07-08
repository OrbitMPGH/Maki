namespace Mangarr.Core.Entities;

public class NamingConfig
{
    public int Id { get; set; }
    public string SeriesFolderFormat { get; set; } = "{Series Title}";
    public string ChapterFileFormat { get; set; } = "{Series Title} Ch.{Chapter Number}";
    public string ChapterFileFormatWithVolume { get; set; } = "{Series Title} Vol.{Volume} Ch.{Chapter Number}";
    public bool RenameChapters { get; set; } = true;
}
