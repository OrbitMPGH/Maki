using System.Globalization;

namespace Maki.Metadata.Embedding;

/// <summary>
/// How often each series lands on a reader's Recommended page: the share of a reader population
/// whose top picks held it. A title on a third of all pages is the same card for everybody, which
/// is what <see cref="RecommenderTuning.CommonWeight"/> pays against.
/// <para>
/// Built by <c>distribution/eval-overlap.py table</c> from the picks <c>eval-reco-labels.cs
/// library --dump-picks</c> writes, as lines of <c>id&lt;TAB&gt;share</c>. Research only for now:
/// nothing in the app registers one, so the penalty is off whatever the weight says.
/// </para>
/// </summary>
public sealed class PageFrequency
{
    private readonly Dictionary<long, double> _share;

    private PageFrequency(Dictionary<long, double> share) => _share = share;

    public int Count => _share.Count;

    /// <summary>Share of pages in [0, 1], 0 for a series the table never saw on one.</summary>
    public double ShareOf(long id) => _share.GetValueOrDefault(id);

    public static PageFrequency Load(string path)
    {
        var share = new Dictionary<long, double>();
        foreach (var line in File.ReadLines(path))
        {
            var cells = line.Split('\t');
            if (cells.Length >= 2
                && long.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                && double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                share[id] = Math.Clamp(value, 0, 1);
            }
        }

        return new PageFrequency(share);
    }

    public static PageFrequency From(IReadOnlyDictionary<long, double> share) =>
        new(share.ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 0, 1)));
}
