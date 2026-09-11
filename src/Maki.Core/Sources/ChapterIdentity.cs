using Maki.Core.Entities;

namespace Maki.Core.Sources;

/// <summary>The shared identity rule for merging a source listing into Maki chapters.</summary>
public static class ChapterIdentity
{
    public static bool Matches(Chapter chapter, SourceChapter sourceChapter)
    {
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
}
