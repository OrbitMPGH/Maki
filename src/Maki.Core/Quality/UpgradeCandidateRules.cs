using Maki.Core.Entities;

namespace Maki.Core.Quality;

/// <summary>
/// Which copies are never upgrade candidates for a file, whatever they score. Shared by the scan, the
/// compare dialog and the download-time guard, so none of them calls a copy an upgrade another refuses.
/// </summary>
public static class UpgradeCandidateRules
{
    /// <summary>
    /// A volume or compilation backing several chapters: replacing it with one chapter's copy would take
    /// the others' pages with it.
    /// </summary>
    public static bool SharedFile(int chaptersOnFile) => chaptersOnFile > 1;

    /// <summary>
    /// The copy on disk came from this listing. Only a different chapter id on the same source (a group's
    /// re-upload) is a new candidate; with no recorded id there is no telling.
    /// </summary>
    public static bool SameCopy(ChapterFile file, string sourceName, string? sourceChapterId) =>
        string.Equals(sourceName, file.SourceName, StringComparison.OrdinalIgnoreCase) &&
        (file.SourceChapterId is null || file.SourceChapterId == sourceChapterId);
}
