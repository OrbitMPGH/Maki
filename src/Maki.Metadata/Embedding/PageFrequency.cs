using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata.Embedding;

/// <summary>
/// How often each series lands on a reader's Recommended page: the share of a reader population
/// whose top picks held it. A title on a third of all pages is the same card for everybody, which
/// is what <see cref="RecommenderTuning.CommonWeight"/> pays against.
/// <para>
/// The app reads the published <c>page-share.db</c> (<see cref="PageShare.PageShareCache"/>), built by
/// <c>distribution/build-page-share.py</c> from the pages <c>eval-reco-labels.cs library
/// --dump-picks</c> writes. The eval harness also reads the <c>id&lt;TAB&gt;share</c> text files
/// <c>distribution/eval-overlap.py table</c> writes.
/// </para>
/// </summary>
public sealed class PageFrequency
{
    private readonly Dictionary<long, double> _share;

    private PageFrequency(Dictionary<long, double> share, DateTime? generatedAt = null)
    {
        _share = share;
        GeneratedAt = generatedAt;
    }

    public int Count => _share.Count;

    public DateTime? GeneratedAt { get; }

    /// <summary>Share of pages in [0, 1], 0 for a series the table never saw on one.</summary>
    public double ShareOf(long id) => _share.GetValueOrDefault(id);

    /// <summary>Reads either format, telling them apart by the SQLite header.</summary>
    public static PageFrequency Load(string path) => IsSqlite(path) ? LoadDatabase(path) : LoadText(path);

    public static PageFrequency From(IReadOnlyDictionary<long, double> share) =>
        new(share.ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 0, 1)));

    private static PageFrequency LoadText(string path)
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

    private static PageFrequency LoadDatabase(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        var share = new Dictionary<long, double>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT series_id, share FROM share";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1) && reader.GetDouble(1) is var value && double.IsFinite(value))
                {
                    share[reader.GetInt64(0)] = Math.Clamp(value, 0, 1);
                }
            }
        }

        DateTime? generatedAt = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'generatedAt'";
            try
            {
                if (DateTime.TryParse(cmd.ExecuteScalar()?.ToString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                {
                    generatedAt = at;
                }
            }
            catch (SqliteException)
            {
            }
        }

        return new PageFrequency(share, generatedAt);
    }

    private static bool IsSqlite(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var file = File.OpenRead(path);
        return file.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }
}
