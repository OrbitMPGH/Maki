using Maki.Core.Entities;

namespace Maki.Core.Reading;

/// <summary>
/// The short human label for a chapter — "Vol.3 Ch.12", "Ch.10.5", or a one-shot's own title.
/// <para>
/// Lives here rather than on a controller because three surfaces render it: the reader manifest,
/// the Home dashboard's reading rails, and the recently-added rail. Rendering it server-side is
/// deliberate — the client would otherwise have to hold the whole chapter list of every series on
/// the page just to turn a chapter id into a label.
/// </para>
/// </summary>
public static class ChapterLabel
{
    public static string For(Chapter chapter) =>
        For(chapter.Number, chapter.Volume, chapter.Title, chapter.IsOneShot);

    /// <summary>
    /// Same label from loose parts, for callers that project chapters into an anonymous shape
    /// rather than materializing whole <see cref="Chapter"/> entities.
    /// </summary>
    public static string For(decimal? number, int? volume, string? title, bool isOneShot)
    {
        if (isOneShot || number is null)
        {
            return title ?? "One-shot";
        }

        var formatted = number.Value.ToString("0.###");
        return volume is { } v ? $"Vol.{v} Ch.{formatted}" : $"Ch.{formatted}";
    }
}
