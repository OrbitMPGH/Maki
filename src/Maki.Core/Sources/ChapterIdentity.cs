using Maki.Core.Entities;

namespace Maki.Core.Sources;

/// <summary>The shared identity rule for merging a source listing into Maki chapters.</summary>
public static class ChapterIdentity
{
    public static bool Matches(Chapter chapter, SourceChapter sourceChapter)
    {
        sourceChapter = Labelled(sourceChapter);
        if (sourceChapter.Number is not null)
        {
            return chapter.Number == sourceChapter.Number &&
                   chapter.Language == sourceChapter.Language &&
                   (chapter.Volume is null || sourceChapter.Volume is null ||
                    chapter.Volume == sourceChapter.Volume);
        }

        return chapter.IsOneShot &&
               chapter.Language == sourceChapter.Language &&
               string.Equals(chapter.Title, sourceChapter.Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unnumbered chapter is told apart by its title, so one with neither number nor title takes
    /// the source's label ("Special", "Extra") as its title. Without it every such chapter of a series
    /// is the same identity and all but the first are dropped.
    /// </summary>
    public static SourceChapter Labelled(SourceChapter chapter) =>
        chapter is { Number: null, Title: null } && !string.IsNullOrWhiteSpace(chapter.NumberRaw)
            ? chapter with { Title = chapter.NumberRaw.Trim() }
            : chapter;
}
