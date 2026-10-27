using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom.Inference;

/// <summary>
/// Recognises a site theme an engine preset already knows, so the wizard can skip guessing.
/// HeanCMS renders nothing recognisable server-side; <c>CustomSourceProbe</c> asks its API instead.
/// </summary>
public static class EngineDetector
{
    public sealed record Match(string Engine, CustomEngineOptions Options);

    public static Match? Run(CustomPage series)
    {
        // The engines build /{seriesPath}/{seriesId}/, so a series page anywhere else can't use one.
        var segments = series.Url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return null;
        }

        var doc = series.Document;
        var options = new CustomEngineOptions { SeriesPath = segments[0] };
        var madara = doc.Body?.ClassList.Any(c => c.Contains("wp-manga", StringComparison.Ordinal)) == true
            || doc.QuerySelector("#manga-chapters-holder, li.wp-manga-chapter") is not null
            || series.Body.Contains("manga_get_chapters", StringComparison.Ordinal);
        if (madara)
        {
            return new Match(CustomEngines.Madara, options with
            {
                ChapterList = MadaraParser.ParseChapters(doc).Count > 0 ? CustomEngineOptions.Inline : CustomEngineOptions.Ajax
            });
        }

        if (doc.QuerySelector("#chapterlist li, div.eplister li") is not null
            || series.Body.Contains("wp-content/themes/mangareader", StringComparison.Ordinal))
        {
            return new Match(CustomEngines.MangaThemesia, options);
        }

        if (doc.QuerySelector("#chapters > a[href*='/chapter/']") is not null)
        {
            return new Match(CustomEngines.Keyoapp, options);
        }

        return null;
    }
}
