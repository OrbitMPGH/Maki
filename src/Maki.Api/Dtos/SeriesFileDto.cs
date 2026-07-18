namespace Maki.Api.Dtos;

/// <summary>
/// A raw file in the series folder cross-referenced with what Maki knows about it:
/// whether a ChapterFile record exists, whether the file is still on disk, and which
/// chapter(s) it is linked to. Powers the series "Files" view so failed/unmatched
/// imports are visible and volume compilations show every chapter they back.
/// </summary>
public record SeriesFileDto(
    string RelativePath,
    string FileName,
    long Size,
    string? SourceName,
    bool OnDisk,
    /// <summary>linked | unlinked | unrecognized | missing</summary>
    string Status,
    /// <summary>What the file name parsed to for display, e.g. "Ch.148", "Vol.3", "Vol.1-2", or null.</summary>
    string? ParsedLabel,
    bool IsVolume,
    /// <summary>Chapter numbers this file is linked to (formatted, sorted), e.g. ["21", "22", "23"].</summary>
    List<string> MappedChapters);
