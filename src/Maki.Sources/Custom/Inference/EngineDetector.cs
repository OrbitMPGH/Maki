using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom.Inference;

/// <summary>Recognises a site theme an engine preset already knows, so the wizard can skip guessing.</summary>
public static class EngineDetector
{
    public sealed record Match(string Engine, CustomEngineOptions Options);

    public static Match? Run(CustomPage series)
    {
        var doc = series.Document;
        var madara = doc.Body?.ClassList.Any(c => c.Contains("wp-manga", StringComparison.Ordinal)) == true
            || doc.QuerySelector("#manga-chapters-holder, li.wp-manga-chapter") is not null
            || series.Body.Contains("manga_get_chapters", StringComparison.Ordinal);
        if (!madara)
        {
            return null;
        }

        // The engine builds /{seriesPath}/{seriesId}/, so a series page anywhere else can't use it.
        var segments = series.Url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return null;
        }

        return new Match(CustomEngines.Madara, new CustomEngineOptions
        {
            SeriesPath = segments[0],
            ChapterList = MadaraParser.ParseChapters(doc).Count > 0 ? CustomEngineOptions.Inline : CustomEngineOptions.Ajax
        });
    }
}
