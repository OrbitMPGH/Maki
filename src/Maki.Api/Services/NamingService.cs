using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Naming;

namespace Maki.Api.Services;

/// <summary>
/// The one place that pairs the stored naming formats with the pure formatter in
/// <c>Maki.Core.Naming</c>. Core has no way to read settings and shouldn't grow one, so every
/// caller that needs a real name (add, import, download, rename) comes through here instead of
/// calling <see cref="FileNameBuilder"/> directly.
///
/// <para>
/// Formats are re-read per call rather than cached: they change rarely, but a stale one writes a
/// wrongly named file to disk, and this is already sitting next to database work that costs far
/// more than a settings lookup.
/// </para>
/// </summary>
public class NamingService(IAppSettings settings)
{
    public async Task<string> SeriesFolderFormatAsync(CancellationToken ct = default) =>
        Valid(await settings.GetAsync(SettingKeys.LibrarySeriesFolderFormat, ct))
            ?? NamingDefaults.SeriesFolderFormat;

    public async Task<string> ChapterFormatAsync(CancellationToken ct = default) =>
        Valid(await settings.GetAsync(SettingKeys.LibraryChapterFormat, ct))
            ?? NamingDefaults.ChapterFormat;

    /// <summary>The folder name a series should have under the configured format.</summary>
    public async Task<string> BuildSeriesFolderNameAsync(Series series, CancellationToken ct = default) =>
        FileNameBuilder.BuildSeriesFolderName(series, await SeriesFolderFormatAsync(ct));

    /// <summary>The chapter's file name, extension included, without any folder.</summary>
    public async Task<string> BuildChapterFileNameAsync(
        Series series, Chapter chapter, CancellationToken ct = default) =>
        FileNameBuilder.BuildChapterFileName(series, chapter, await ChapterFormatAsync(ct));

    /// <summary>
    /// The chapter file's path relative to the root folder. Uses the series' existing
    /// <see cref="Series.FolderName"/>, never the folder format — see <see cref="FileNameBuilder"/>.
    /// </summary>
    public async Task<string> BuildChapterRelativePathAsync(
        Series series, Chapter chapter, CancellationToken ct = default) =>
        FileNameBuilder.BuildRelativePath(series, chapter, await ChapterFormatAsync(ct));

    /// <summary>
    /// A stored format that no longer validates (a token removed by an upgrade, a hand-edited
    /// database) falls back to the default rather than producing junk names. Saving is what
    /// enforces validity; this is the safety net behind it.
    /// </summary>
    private static string? Valid(string? format) =>
        !string.IsNullOrWhiteSpace(format) && NamingFormatter.Validate(format).Count == 0 ? format : null;
}
