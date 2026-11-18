using System.Globalization;
using Maki.Core.Entities;

namespace Maki.Core.Sources;

/// <summary>What one mapping's listing looks like once its folds are applied.</summary>
/// <param name="Chapters">The listing, each folded run of parts replaced by one chapter numbered as its floor.</param>
/// <param name="Parts">Per such chapter (by its <see cref="SourceChapter.SourceChapterId"/>), its parts as stored JSON.</param>
/// <param name="Broken">Folds whose parts the listing no longer has, all of them, as the same uploads.</param>
public sealed record FoldedListing(
    IReadOnlyList<SourceChapter> Chapters, IReadOnlyDictionary<string, string> Parts, IReadOnlyList<ChapterFold> Broken);

/// <summary>
/// Rewrites a mapping's listing through its <see cref="ChapterFold"/>s, before sync matches it to
/// chapter rows. A complete listing with every part of a fold lists them as chapter f; one missing a
/// part, or listing another upload under its id, breaks the fold, and the parts are rows again. A
/// partial (newest-first) listing never breaks one: it leaves out the parts it has, so nothing is
/// added for them. When the mapping lists the whole f itself, the parts are only left out.
/// </summary>
public static class ChapterFoldListing
{
    public static FoldedListing Rewrite(IReadOnlyList<SourceChapter> listing, IReadOnlyList<ChapterFold> folds, bool complete)
    {
        if (folds.Count == 0)
        {
            return new FoldedListing(listing, new Dictionary<string, string>(), []);
        }

        var chapters = listing.ToList();
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        var broken = new List<ChapterFold>();
        foreach (var fold in folds)
        {
            var wanted = fold.Parts;
            var listed = wanted
                .Select(p => chapters.FirstOrDefault(c =>
                    c.SourceChapterId == p.SourceChapterId && c.Number == p.Number && SourceLanguages.Same(c.Language, fold.Language)))
                .ToList();
            if (wanted.Count == 0 || listed.Any(l => l is null))
            {
                if (complete)
                {
                    broken.Add(fold);
                }
                else
                {
                    chapters.RemoveAll(c => listed.Contains(c));
                }

                continue;
            }

            var present = listed.OfType<SourceChapter>().ToList();
            var index = chapters.IndexOf(present[0]);
            chapters.RemoveAll(c => present.Contains(c));
            var ownWhole = chapters.Any(c => c.Number == fold.Floor && SourceLanguages.Same(c.Language, fold.Language));
            if (ownWhole)
            {
                continue;
            }

            var first = present[0];
            var whole = first with
            {
                Number = fold.Floor,
                NumberRaw = fold.Floor.ToString(CultureInfo.InvariantCulture),
                Title = null,
                ReleaseDate = present.Select(p => p.ReleaseDate).Min(),
                Locked = present.Any(p => p.Locked),
                PageCount = present.All(p => p.PageCount is > 0) ? present.Sum(p => p.PageCount) : null,
            };
            chapters.Insert(Math.Min(index, chapters.Count), whole);
            parts[whole.SourceChapterId] = fold.PartsJson;
        }

        return new FoldedListing(chapters, parts, broken);
    }
}
