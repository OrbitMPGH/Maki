#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

// Grades reader-cohorts.db on HELD-OUT readers, and answers the two questions the two surfaces
// ask separately, because they can fail independently.
//
// Run:
//   dotnet run distribution/eval-reader-cohorts.cs -- --fold 0/4 --artifact .artifacts/reader-cohorts-fold0.db
//   dotnet run distribution/eval-reader-cohorts.cs -- --fold 0/4 --placement lift,rate,cosine
//
// THE BADGE: is a cohort mean worth showing?
// A cohort mean has to predict a held-out reader's own score better than the plain item mean does.
// If it does not, the badge is a global average wearing the words "readers like you", which is the
// dishonest failure this whole feature has to avoid. Baselines, in increasing strength:
//   global mean            - one number for the catalogue
//   item mean              - what everybody scored this series (i.e. what MangaBaka already shows)
//   item mean + reader bias- plus how far above their own average this reader rates
//   cohort mean            - what the reader's own cohorts scored it   <- the badge
//   cohort mean + bias     - the same, recalibrated to this reader's scale
// The badge ships the RAW cohort mean, because a number shown next to MangaBaka's has to be on the
// same scale as MangaBaka's. The +bias rows are here to say how much of the remaining error is the
// reader's personal scale rather than the cohort being wrong.
//
// THE RAIL: is it a chart?
// Ranking a catalogue by any per-reader score tends to return the same famous list to everybody.
// Measured on a predicted-score ranking of this data the top 40 was 87.6% identical across readers,
// with median popularity rank 688 of ~126,000. So `overlap` and `pop` are reported beside recall,
// and `lift` (cohort rate divided by the global rate) is compared against `rate` (the cohort rate
// alone) as the mechanism that is supposed to fix it. A variant that wins recall while overlap
// stays near 1 has not earned anything.

using System.Globalization;
using Microsoft.Data.Sqlite;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var artifactPath = Path.Combine(".artifacts", "reader-cohorts.db");
var workPath = Path.Combine(".artifacts", "coread-graph.db");
var dumpPath = Path.Combine(".artifacts", "mangabaka.full.db");
var foldOut = 0;
var foldCount = 4;
var requests = 2000;
var topCohorts = 5;
var topK = 40;
var minBadgeRaters = 20;
var bootstrap = 1000;
var seed = 20260902;
var placements = new List<string> { "lift", "rate" };
var gammas = new List<double> { 0.0, 0.25, 0.5, 0.75, 1.0 };

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--artifact": artifactPath = args[++i]; break;
        case "--work": workPath = args[++i]; break;
        case "--dump": dumpPath = args[++i]; break;
        case "--requests": requests = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--top-cohorts": topCohorts = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--top": topK = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-badge-raters": minBadgeRaters = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--bootstrap": bootstrap = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--rng": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--placement":
            placements = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            break;
        case "--fold":
            var parts = args[++i].Split('/');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out foldOut)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out foldCount)
                || foldCount < 2 || foldOut < 0 || foldOut >= foldCount)
            {
                Console.WriteLine($"error: --fold wants k/n with n >= 2 and 0 <= k < n, not '{args[i]}'.");
                return 2;
            }

            break;
        default:
            Console.WriteLine($"error: unknown argument '{args[i]}'");
            return 2;
    }
}

foreach (var (name, path) in new[] { ("artifact", artifactPath), ("working database", workPath), ("dump", dumpPath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"error: no {name} at {path}");
        return 2;
    }
}

// -------------------------------------------------------------------------------------------------
// Load the artifact, and refuse to grade one that saw this fold
// -------------------------------------------------------------------------------------------------

var meta = new Dictionary<string, string>(StringComparer.Ordinal);
var cohortReaders = new List<int>();
var cohortVectors = new List<float[]>();
var cohortItems = new Dictionary<long, (int Completions, int Raters, double? Mean)>[0];
var globalRow = new Dictionary<long, (int Completions, int Raters, double? Mean)>(120_000);

using (var conn = new SqliteConnection($"Data Source={artifactPath};Mode=ReadOnly;Pooling=False"))
{
    conn.Open();

    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT key, value FROM meta";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            meta[reader.GetString(0)] = reader.GetString(1);
        }
    }

    // Holding a channel out is a switch; holding a MODEL out is a different artifact. An artifact
    // that grouped this fold's readers has already seen the answers.
    var trainingFold = meta.GetValueOrDefault("trainingFold", "");
    var sawThisFold = trainingFold.Equals("all", StringComparison.OrdinalIgnoreCase)
        || trainingFold.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(foldOut.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
    if (sawThisFold)
    {
        Console.WriteLine(
            $"error: this artifact's trainingFold is '{trainingFold}', which includes fold {foldOut}.");
        Console.WriteLine($"       Build one with `--fold-out {foldOut}/{foldCount}` and grade that.");
        return 1;
    }

    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT cohort, readers, scale, vec FROM cohort ORDER BY cohort";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cohortReaders.Add(reader.GetInt32(1));
            var scale = (float)reader.GetDouble(2);
            var blob = (byte[])reader[3];
            var vec = new float[blob.Length];
            for (var d = 0; d < blob.Length; d++)
            {
                vec[d] = (sbyte)blob[d] * scale;
            }

            cohortVectors.Add(vec);
        }
    }

    cohortItems = new Dictionary<long, (int, int, double?)>[cohortReaders.Count];
    for (var c = 0; c < cohortItems.Length; c++)
    {
        cohortItems[c] = new Dictionary<long, (int, int, double?)>(20_000);
    }

    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT cohort, id, completions, raters, mean FROM cohort_item";
        cmd.CommandTimeout = 600;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var c = reader.GetInt32(0);
            if (c < 0 || c >= cohortItems.Length)
            {
                continue;
            }

            cohortItems[c][reader.GetInt64(1)] =
                (reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDouble(4));
        }
    }

    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT id, completions, raters, mean FROM item_global";
        cmd.CommandTimeout = 600;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            globalRow[reader.GetInt64(0)] =
                (reader.GetInt32(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetDouble(3));
        }
    }
}

var cohorts = cohortReaders.Count;
var trainedReaders = long.Parse(meta.GetValueOrDefault("trainedReaders", "0"), CultureInfo.InvariantCulture);

Console.WriteLine($"artifact : {artifactPath}");
Console.WriteLine(
    $"           {cohorts} cohorts over {trainedReaders:N0} readers, trainingFold '{meta.GetValueOrDefault("trainingFold", "?")}'");
Console.WriteLine($"           {globalRow.Count:N0} global rows, {cohortItems.Sum(c => c.Count):N0} cohort rows");

var withMean = globalRow.Values.Count(v => v.Mean is not null);
var cohortWithMean = cohortItems.Sum(c => c.Values.Count(v => v.Mean is not null));
var distinctCohortItems = new HashSet<long>();
var distinctRatedCohortItems = new HashSet<long>();
foreach (var table in cohortItems)
{
    foreach (var (id, row) in table)
    {
        distinctCohortItems.Add(id);
        if (row.Mean is not null)
        {
            distinctRatedCohortItems.Add(id);
        }
    }
}

Console.WriteLine(
    $"coverage : {withMean:N0} global rows carry a mean; {cohortWithMean:N0} cohort rows do, " +
    $"over {distinctRatedCohortItems.Count:N0} distinct series ({distinctCohortItems.Count:N0} with any cohort row)");

// -------------------------------------------------------------------------------------------------
// Popularity ranks, for the `pop` column
// -------------------------------------------------------------------------------------------------

var crossRef = new Dictionary<long, long>(150_000);
var popularityRank = new Dictionary<long, int>(150_000);

using (var conn = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText =
        """
        SELECT source_anilist_id, id
        FROM series
        WHERE state = 'active' AND type != 'novel' AND source_anilist_id IS NOT NULL
        ORDER BY COALESCE(popularity_global_current, 2147483647)
        """;
    cmd.CommandTimeout = 900;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var mangaBakaId = reader.GetInt64(1);
        if (crossRef.TryAdd(reader.GetInt64(0), mangaBakaId))
        {
            // The query is already ordered by popularity, so insertion order IS the rank. Lower is
            // more famous, the same direction every other eval here reports `pop` in.
            popularityRank.TryAdd(mangaBakaId, popularityRank.Count + 1);
        }
    }
}

Console.WriteLine($"dump     : {crossRef.Count:N0} cross-referenced series ranked by popularity");

// -------------------------------------------------------------------------------------------------
// Held-out readers
// -------------------------------------------------------------------------------------------------

var heldOut = new Dictionary<int, List<(long Id, int Score)>>();

using (var conn = new SqliteConnection($"Data Source={workPath};Mode=ReadOnly;Pooling=False"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT user_id, media_id, score FROM user_entry WHERE status = 'COMPLETED'";
    cmd.CommandTimeout = 1800;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var user = (int)reader.GetInt64(0);
        if (UserFold.Of(user, foldCount) != foldOut)
        {
            continue;
        }

        if (!crossRef.TryGetValue(reader.GetInt64(1), out var mangaBakaId))
        {
            continue;
        }

        if (!heldOut.TryGetValue(user, out var list))
        {
            heldOut[user] = list = [];
        }

        list.Add((mangaBakaId, reader.GetInt32(2)));
    }
}

// A reader needs enough visible history to be placed and enough held-out scores to be graded.
var graded = heldOut
    .Where(kv => kv.Value.Count >= 20 && kv.Value.Count(e => e.Score > 0) >= 10)
    .OrderBy(kv => kv.Key)
    .Take(requests)
    .ToList();

Console.WriteLine(
    $"readers  : fold {foldOut}/{foldCount} holds {heldOut.Count:N0}; {graded.Count:N0} graded " +
    $"( >= 20 finishes, >= 10 of them scored )");
Console.WriteLine();

if (graded.Count == 0)
{
    Console.WriteLine("error: nothing gradeable in this fold.");
    return 1;
}

var globalMean = globalRow.Values.Where(v => v.Mean is not null).Average(v => v.Mean!.Value);

// -------------------------------------------------------------------------------------------------
// Grade
// -------------------------------------------------------------------------------------------------

var rowsPerReader = new Dictionary<string, List<double>>(StringComparer.Ordinal);
var railRows = new List<(string Placement, string Ranking, double Recall, double Pop, double Overlap, double Covered)>();

foreach (var placement in placements)
{
    var badgeErrors = new Dictionary<string, List<double>>(StringComparer.Ordinal)
    {
        ["global mean"] = [],
        ["item mean"] = [],
        ["item mean + bias"] = [],
        ["cohort mean"] = [],
        ["cohort mean + bias"] = [],
    };

    var topByGamma = gammas.ToDictionary(g => g, _ => new List<long[]>(graded.Count));
    var recallByGamma = gammas.ToDictionary(g => g, _ => new List<double>(graded.Count));
    var placedCount = 0;

    // The badge falls back to the item mean whenever the reader's cohorts have too few raters, so
    // the pooled MAE mixes predictions the cohort made with predictions it did not. These two hold
    // only the ones it actually answered.
    var firedCohort = new List<double>();
    var firedItem = new List<double>();
    var divergence = new List<double>();

    foreach (var (user, entries) in graded)
    {
        // Deterministic 80/20 within the reader, so every placement mode sees the same split.
        var visible = new List<(long Id, int Score)>(entries.Count);
        var hidden = new List<(long Id, int Score)>(entries.Count / 4);
        foreach (var entry in entries)
        {
            if (Split(user, entry.Id) < 0.2)
            {
                hidden.Add(entry);
            }
            else
            {
                visible.Add(entry);
            }
        }

        if (visible.Count < 5 || hidden.Count(h => h.Score > 0) == 0)
        {
            continue;
        }

        var weights = Place(
            placement, visible, cohortItems, cohortReaders, globalRow, cohortVectors, topCohorts, trainedReaders);
        if (weights.Count == 0)
        {
            continue;
        }

        placedCount++;

        // The reader's own offset, from what is VISIBLE only. Reading it off the held-out half would
        // be scoring the answer key.
        var visibleScored = visible.Where(v => v.Score > 0).ToList();
        var readerBias = 0.0;
        if (visibleScored.Count >= 5)
        {
            var expected = 0.0;
            var seen = 0;
            foreach (var (id, score) in visibleScored)
            {
                if (globalRow.TryGetValue(id, out var row) && row.Mean is { } mean)
                {
                    expected += score - mean;
                    seen++;
                }
            }

            if (seen >= 5)
            {
                readerBias = expected / seen;
            }
        }

        foreach (var (id, score) in hidden)
        {
            if (score <= 0)
            {
                continue;
            }

            var itemMean = globalRow.TryGetValue(id, out var row) && row.Mean is { } m ? m : globalMean;
            var resolved = CohortMean(id, weights, cohortItems, minBadgeRaters);
            var cohortMean = resolved ?? itemMean;

            badgeErrors["global mean"].Add(Math.Abs(score - globalMean));
            badgeErrors["item mean"].Add(Math.Abs(score - itemMean));
            badgeErrors["item mean + bias"].Add(Math.Abs(score - (itemMean + readerBias)));
            badgeErrors["cohort mean"].Add(Math.Abs(score - cohortMean));
            badgeErrors["cohort mean + bias"].Add(Math.Abs(score - (cohortMean + readerBias)));

            if (resolved is not null)
            {
                firedCohort.Add(Math.Abs(score - cohortMean));
                firedItem.Add(Math.Abs(score - itemMean));
                // Accuracy is not the only thing a second badge has to earn. If the cohort number
                // rounds to the same thing MangaBaka already shows, the badge claims a
                // personalisation the reader cannot see.
                divergence.Add(Math.Abs(cohortMean - itemMean));
            }
        }

        // Rail: rank everything the reader has not finished, by lift and by rate.
        var owned = new HashSet<long>(visible.Select(v => v.Id));
        var answers = new HashSet<long>(hidden.Select(h => h.Id));
        foreach (var gamma in gammas)
        {
            var picks = RankCandidates(
                weights, cohortItems, cohortReaders, globalRow, owned, topK, gamma, trainedReaders);
            topByGamma[gamma].Add(picks);
            recallByGamma[gamma].Add(
                answers.Count == 0 ? 0 : picks.Count(answers.Contains) / (double)Math.Min(topK, answers.Count));
        }
    }

    Console.WriteLine($"=== placement: {placement} ===");
    Console.WriteLine($"placed   : {placedCount:N0} of {graded.Count:N0} readers");
    Console.WriteLine();
    Console.WriteLine("badge, mean absolute error against the reader's own held-out score (POINT_100):");
    Console.WriteLine($"  {"model",-22} {"MAE",8} {"RMSE",8} {"n",10}");
    foreach (var (name, errors) in badgeErrors)
    {
        if (errors.Count == 0)
        {
            continue;
        }

        var mae = errors.Average();
        var rmse = Math.Sqrt(errors.Average(e => e * e));
        Console.WriteLine($"  {name,-22} {mae,8:F3} {rmse,8:F3} {errors.Count,10:N0}");
        rowsPerReader[$"{placement}|{name}"] = errors;
    }

    if (firedCohort.Count > 0)
    {
        var share = firedCohort.Count / (double)badgeErrors["item mean"].Count;
        Console.WriteLine(
            $"  cohort answered {firedCohort.Count:N0} of {badgeErrors["item mean"].Count:N0} " +
            $"predictions ({share:P1}); on those alone MAE {firedCohort.Average():F3} against the " +
            $"item mean's {firedItem.Average():F3}");
        rowsPerReader[$"{placement}|fired cohort"] = firedCohort;
        rowsPerReader[$"{placement}|fired item"] = firedItem;

        // The modal renders every score as `x / 10` to one decimal, so a gap under 0.5 POINT_100 is
        // literally the same glyphs and a gap under 1.0 is one decimal place.
        divergence.Sort();
        var median = divergence[divergence.Count / 2];
        var p90 = divergence[(int)(divergence.Count * 0.90)];
        Console.WriteLine(
            $"  divergence from the item mean: median {median:F2}, p90 {p90:F2} points; " +
            $"{divergence.Count(d => d >= 5) / (double)divergence.Count:P1} differ by >= 5 " +
            $"(half a star), {divergence.Count(d => d >= 10) / (double)divergence.Count:P1} by >= 10");
    }

    Console.WriteLine();
    foreach (var gamma in gammas)
    {
        railRows.Add((placement, $"gamma {gamma:F2}",
            recallByGamma[gamma].Count == 0 ? 0 : recallByGamma[gamma].Average(),
            MedianPop(topByGamma[gamma], popularityRank), Overlap(topByGamma[gamma]), topByGamma[gamma].Count));
    }
}

Console.WriteLine("rail, top-40 over series the reader has not finished:");
Console.WriteLine($"  {"placement",-12} {"ranking",-12} {"recall",8} {"pop",10} {"overlap",9}");
foreach (var (placement, ranking, recall, pop, overlap, _) in railRows)
{
    Console.WriteLine($"  {placement,-12} {ranking,-12} {recall,8:F4} {pop,10:N0} {overlap,9:F3}");
}

Console.WriteLine();
Console.WriteLine("  gamma   how much of the global rate is divided back out: 0 is the raw cohort");
Console.WriteLine("          rate, 1 is pure lift");
Console.WriteLine("  recall  share of the reader's held-out finishes recovered in the top 40");
Console.WriteLine("  pop     median popularity rank of the picks; LOWER means more famous");
Console.WriteLine("  overlap mean pairwise share of the top 40 two different readers have in common;");
Console.WriteLine("          near 1.0 means the rail is a chart, not a recommendation");

// -------------------------------------------------------------------------------------------------
// Paired bootstrap, cohort mean against the item mean it has to beat
// -------------------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine($"paired bootstrap, {bootstrap:N0} resamples over graded predictions:");
foreach (var placement in placements)
{
    if (!rowsPerReader.TryGetValue($"{placement}|cohort mean", out var cohortErr)
        || !rowsPerReader.TryGetValue($"{placement}|item mean", out var itemErr)
        || cohortErr.Count != itemErr.Count)
    {
        continue;
    }

    var (point, low, high) = Bootstrap(itemErr, cohortErr, bootstrap, seed);
    var verdict = high < 0 ? "cohort mean WINS" : low > 0 ? "cohort mean LOSES" : "indistinguishable";
    Console.WriteLine(
        $"  {placement,-12} all           {point,8:F4}  95% [{low,7:F4}, {high,7:F4}]  {verdict}");

    if (rowsPerReader.TryGetValue($"{placement}|fired cohort", out var firedC)
        && rowsPerReader.TryGetValue($"{placement}|fired item", out var firedI))
    {
        var (p2, l2, h2) = Bootstrap(firedI, firedC, bootstrap, seed);
        var v2 = h2 < 0 ? "cohort mean WINS" : l2 > 0 ? "cohort mean LOSES" : "indistinguishable";
        Console.WriteLine(
            $"  {placement,-12} where it fired{p2,8:F4}  95% [{l2,7:F4}, {h2,7:F4}]  {v2}");
    }
}

return 0;

// -------------------------------------------------------------------------------------------------

/// <summary>Deterministic per (reader, series) split, so every variant grades the same held-out half.</summary>
static double Split(int user, long id)
{
    var hash = 2166136261u;
    var value = (ulong)((long)user * 1_000_003L) ^ (ulong)id;
    for (var i = 0; i < 8; i++)
    {
        hash = (hash ^ (byte)(value >> (i * 8))) * 16777619u;
    }

    return hash / (double)uint.MaxValue;
}

/// <summary>
/// Where this reader sits against the shipped cohorts, from their visible history alone. The whole
/// feature turns on this being computable from group aggregates: nothing here reads a person.
/// </summary>
static Dictionary<int, double> Place(
    string mode,
    List<(long Id, int Score)> visible,
    Dictionary<long, (int Completions, int Raters, double? Mean)>[] cohortItems,
    List<int> cohortReaders,
    Dictionary<long, (int Completions, int Raters, double? Mean)> globalRow,
    List<float[]> cohortVectors,
    int topCohorts,
    long trainedReaders)
{
    var scores = new double[cohortItems.Length];

    foreach (var (id, _) in visible)
    {
        if (!globalRow.TryGetValue(id, out var global) || global.Completions <= 0)
        {
            continue;
        }

        var globalRate = global.Completions / (double)Math.Max(1, trainedReaders);
        // Rare titles say far more about a reader than the ones everybody finishes.
        var idf = Math.Log(Math.Max(1.0, trainedReaders / (double)(1 + global.Completions)));

        for (var c = 0; c < cohortItems.Length; c++)
        {
            if (!cohortItems[c].TryGetValue(id, out var row) || row.Completions <= 0)
            {
                continue;
            }

            var rate = row.Completions / (double)Math.Max(1, cohortReaders[c]);
            scores[c] += mode == "lift" ? idf * (rate / (globalRate + 1e-9)) : idf * rate;
        }
    }

    var order = Enumerable.Range(0, scores.Length)
        .Where(c => scores[c] > 0)
        .OrderByDescending(c => scores[c])
        .Take(topCohorts)
        .ToArray();

    if (order.Length == 0)
    {
        return new Dictionary<int, double>();
    }

    // Subtracting the weakest kept cohort keeps the mix from flattening into "all of them equally",
    // which is just the global average again.
    var floor = scores[order[^1]];
    var total = order.Sum(c => scores[c] - floor);
    var weights = new Dictionary<int, double>(order.Length);
    foreach (var c in order)
    {
        weights[c] = total > 0 ? (scores[c] - floor) / total : 1.0 / order.Length;
    }

    return weights;
}

/// <summary>
/// Support-weighted mean across the reader's cohorts. Weighted by raters as well as by cohort
/// affinity, so a cohort that barely rated the title cannot outvote one that did.
/// </summary>
static double? CohortMean(
    long id,
    Dictionary<int, double> weights,
    Dictionary<long, (int Completions, int Raters, double? Mean)>[] cohortItems,
    int minRaters)
{
    var numerator = 0.0;
    var denominator = 0.0;
    var raters = 0;

    foreach (var (c, w) in weights)
    {
        if (!cohortItems[c].TryGetValue(id, out var row) || row.Mean is not { } mean || row.Raters <= 0)
        {
            continue;
        }

        numerator += w * row.Raters * mean;
        denominator += w * row.Raters;
        raters += row.Raters;
    }

    return denominator > 0 && raters >= minRaters ? numerator / denominator : null;
}

/// <summary>
/// Cohort completion rate with the global rate divided back out to the power
/// <paramref name="gamma"/>. This is the one dial between the two failure modes the table shows:
/// gamma 0 is the raw rate, which returns the popularity chart, and gamma 1 is pure lift, which
/// returns titles so obscure that almost nobody goes on to finish them. The shipping value has to
/// come off the sweep rather than off the argument that lift is obviously right.
/// </summary>
static long[] RankCandidates(
    Dictionary<int, double> weights,
    Dictionary<long, (int Completions, int Raters, double? Mean)>[] cohortItems,
    List<int> cohortReaders,
    Dictionary<long, (int Completions, int Raters, double? Mean)> globalRow,
    HashSet<long> owned,
    int topK,
    double gamma,
    long trainedReaders)
{
    var scored = new Dictionary<long, double>(4096);

    foreach (var (c, w) in weights)
    {
        foreach (var (id, row) in cohortItems[c])
        {
            if (owned.Contains(id) || row.Completions <= 0)
            {
                continue;
            }

            if (!globalRow.TryGetValue(id, out var global) || global.Completions <= 0)
            {
                continue;
            }

            var rate = row.Completions / (double)Math.Max(1, cohortReaders[c]);
            var globalRate = global.Completions / (double)Math.Max(1, trainedReaders);
            var value = gamma <= 0 ? rate : rate / Math.Pow(globalRate, gamma);
            scored[id] = scored.GetValueOrDefault(id) + (w * value);
        }
    }

    return scored.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(topK).Select(kv => kv.Key).ToArray();
}

static double MedianPop(List<long[]> lists, Dictionary<long, int> ranks)
{
    var all = new List<int>(lists.Count * 40);
    foreach (var list in lists)
    {
        foreach (var id in list)
        {
            if (ranks.TryGetValue(id, out var rank))
            {
                all.Add(rank);
            }
        }
    }

    if (all.Count == 0)
    {
        return 0;
    }

    all.Sort();
    return all[all.Count / 2];
}

/// <summary>
/// Mean pairwise overlap of the top lists, over a bounded sample of pairs. This is the column that
/// separates "personalised" from "the same chart with the reader's name on it".
/// </summary>
static double Overlap(List<long[]> lists)
{
    if (lists.Count < 2)
    {
        return 0;
    }

    var rng = new Random(12345);
    var total = 0.0;
    var pairs = Math.Min(4000, lists.Count * (lists.Count - 1) / 2);
    for (var p = 0; p < pairs; p++)
    {
        var a = lists[rng.Next(lists.Count)];
        var b = lists[rng.Next(lists.Count)];
        if (ReferenceEquals(a, b) || a.Length == 0 || b.Length == 0)
        {
            p--;
            continue;
        }

        var set = new HashSet<long>(a);
        total += b.Count(set.Contains) / (double)Math.Min(a.Length, b.Length);
    }

    return total / pairs;
}

/// <summary>
/// Paired bootstrap over the per-prediction absolute errors. Negative means the second arm is
/// closer to the truth.
/// </summary>
static (double Point, double Low, double High) Bootstrap(
    List<double> baseline, List<double> candidate, int resamples, int seed)
{
    var diff = new double[baseline.Count];
    for (var i = 0; i < diff.Length; i++)
    {
        diff[i] = candidate[i] - baseline[i];
    }

    var point = diff.Average();
    var rng = new Random(seed);
    var means = new double[resamples];
    for (var r = 0; r < resamples; r++)
    {
        var sum = 0.0;
        for (var i = 0; i < diff.Length; i++)
        {
            sum += diff[rng.Next(diff.Length)];
        }

        means[r] = sum / diff.Length;
    }

    Array.Sort(means);
    return (point, means[(int)(resamples * 0.025)], means[(int)(resamples * 0.975)]);
}

/// <summary>
/// Which evaluation fold a reader belongs to. MUST stay byte-identical to the copies in
/// build-taste-vectors.cs, build-reader-cohorts.cs and eval-reco-labels.cs: a grader that partitions
/// readers differently from the builder grades an artifact on readers it was built from while
/// honestly reporting that it did not.
/// </summary>
file static class UserFold
{
    public static int Of(long userId, int folds)
    {
        var hash = 2166136261u;
        var value = (ulong)userId;
        for (var i = 0; i < 8; i++)
        {
            hash = (hash ^ (byte)(value >> (i * 8))) * 16777619u;
        }

        return (int)(hash % (uint)folds);
    }
}
