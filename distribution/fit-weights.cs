#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

// Fits the hybrid score's channel coefficients against held-out labels, instead of sweeping them one
// at a time.
//
// Run:
//   dotnet run distribution/eval-reco-labels.cs -- single --labels mu-human --requests 800 \
//       --dump-features .artifacts/eval/features-mu.csv default
//   dotnet run distribution/fit-weights.cs -- .artifacts/eval/features-mu.csv
//
// WHY THIS EXISTS
// Nine coefficients, each chosen by a coordinate sweep, several of them years apart and all of them
// before the behavioural channel existed. A coordinate sweep cannot see an interaction, so fitting
// them together is worth having even though - see below - what it mostly found was the label sets'
// own biases rather than a better ranking.
//
// The lead that prompted it was the author channel measuring BETTER switched off (+0.0045 nDCG on
// three-seed MangaUpdates pairs). That turned out to be an artifact of the grader, not a redundancy:
// on held-out READERS, halving that weight is -0.0089 (95% [-0.0129, -0.0051]) and removing it is
// -0.0255 (95% [-0.0321, -0.0192]). Pair label sets under-represent same-creator recommendations,
// because a human submitting "if you liked X, try Y" reaches for a DIFFERENT author - that is what
// makes it a recommendation worth submitting - while a real reader who liked something goes and
// reads more by the same person.
//
// WHAT IT FITS
// Pairwise logistic regression, the standard reduction of ranking to classification: for a candidate
// the labels call relevant and one they do not, both from the SAME request, the score difference
// should be positive. Optimizing that optimizes the ordering, which is what nDCG reads, rather than
// optimizing a score nobody looks at in absolute terms.
//
// Pairs come only from within a request. Two candidates from different requests never competed, and
// pairing them would teach the model to rank a hard request's winner below an easy request's loser.
//
// WHAT IT DELIBERATELY DOES NOT DO
// It does not fit the popularity term. That column is in the file, and a second fit that includes it
// is printed as a DIAGNOSTIC: if the coefficients move a lot when popularity is available, the
// channels were partly standing in for fame and the first fit is the honest one. Shipping a fitted
// popularity coefficient would bake the thing every table in this codebase is read against straight
// into the score.
//
// READ THE OUTPUT AS A HYPOTHESIS. It is fitted on one label set, and the label sets disagree by
// design. Take the variant string it prints, run it through eval-reco-labels.cs against `mu-human`,
// `reco` and a held-out `library`, and believe the intervals rather than this.

using System.Globalization;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var path = args.Length > 0 ? args[0] : Path.Combine(".artifacts", "eval", "features.csv");
var epochs = 40;
var pairsPerRequest = 400;
var l2 = 1e-4;
var learningRate = 0.5;
var seed = 20260829;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--epochs": epochs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--pairs": pairsPerRequest = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--l2": l2 = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--lr": learningRate = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--rng": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default:
            Console.WriteLine($"error: unknown argument '{args[i]}'");
            return 2;
    }
}

if (!File.Exists(path))
{
    Console.WriteLine($"error: no feature file at {path}");
    Console.WriteLine("  Produce one with eval-reco-labels.cs --dump-features <path>.");
    return 2;
}

// Column order is the header eval-reco-labels.cs writes. Popularity is last and is held out of the
// shipped fit.
string[] names = ["semantic", "genre", "tag", "author", "quality", "graph", "coread", "taste", "distinct"];
const int PopColumn = 9;
var featureCount = names.Length;

var byRequest = new Dictionary<int, (List<double[]> Positive, List<double[]> Negative)>();
var rows = 0;
using (var reader = new StreamReader(path))
{
    _ = reader.ReadLine(); // header
    while (reader.ReadLine() is { } line)
    {
        var parts = line.Split(',');
        if (parts.Length < featureCount + 3)
        {
            continue;
        }

        var request = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var label = parts[1] == "1";
        var vector = new double[featureCount + 1];
        for (var f = 0; f <= featureCount; f++)
        {
            vector[f] = double.Parse(parts[f + 2], CultureInfo.InvariantCulture);
        }

        if (!byRequest.TryGetValue(request, out var bucket))
        {
            byRequest[request] = bucket = ([], []);
        }

        (label ? bucket.Positive : bucket.Negative).Add(vector);
        rows++;
    }
}

// A request with no positive in its pool teaches nothing: every pair would be negative against
// negative. They are dropped rather than counted, and how many there were is worth printing, since a
// large share means the pool is missing the answers rather than mis-ordering them.
var usable = byRequest.Where(r => r.Value.Positive.Count > 0 && r.Value.Negative.Count > 0).ToList();

Console.WriteLine($"file     : {path}");
Console.WriteLine($"rows     : {rows:N0} candidates over {byRequest.Count:N0} requests");
Console.WriteLine($"usable   : {usable.Count:N0} requests have both a positive and a negative in the pool");
Console.WriteLine();

if (usable.Count == 0)
{
    Console.WriteLine("error: nothing to fit.");
    return 1;
}

// A COLUMN THAT NEVER VARIES CANNOT BE FITTED, and a fit reports 0 for it rather than an error.
// Both modes that produce these files have one: `single` never builds a seed query, so `distinct` is
// always 0; `library` force-disables the co-read channel because those lists are its training data,
// so `coread` is always 0. Reading either as "the fit found this channel worthless" is wrong, and it
// looks exactly like a finding.
for (var f = 0; f < featureCount; f++)
{
    var min = double.MaxValue;
    var max = double.MinValue;
    foreach (var (_, bucket) in usable)
    {
        foreach (var vector in bucket.Positive.Concat(bucket.Negative))
        {
            min = Math.Min(min, vector[f]);
            max = Math.Max(max, vector[f]);
        }
    }

    if (max - min < 1e-12)
    {
        Console.WriteLine(
            $"warning: '{names[f]}' is constant at {min:F3} in this file, so its coefficient is not "
            + "fitted. Do not read the 0 below as a result.");
    }
}

Console.WriteLine();

var shipped = new Dictionary<string, double>
{
    ["semantic"] = 3.0,
    ["genre"] = 1.0,
    ["tag"] = 2.0,
    ["author"] = 0.75,
    ["quality"] = 0.5,
    ["graph"] = 0.6,
    ["coread"] = 0.15,
    ["taste"] = 1.5,
    ["distinct"] = 0.0,
};

var baseline = names.Select(n => shipped[n]).ToArray();
Console.WriteLine($"shipped weights rank {PairAccuracy(baseline, includePop: false):P2} of pairs correctly.");
Console.WriteLine();

var fitted = Fit(includePop: false);
var withPop = Fit(includePop: true);

Report("FITTED (popularity held out - this is the one to test)", fitted, includePop: false);
Console.WriteLine();
Report("DIAGNOSTIC (popularity available to the fit)", withPop, includePop: true);
Console.WriteLine();

// The score is linear, so its ranking is invariant to a global scale. Rescaling to the shipped
// semantic coefficient is what makes the fitted numbers readable next to the ones already written
// down, rather than a vector of unfamiliar magnitudes that says nothing at a glance.
var scale = Math.Abs(fitted[0]) > 1e-9 ? shipped["semantic"] / fitted[0] : 1.0;
var scaled = fitted.Select(w => w * scale).ToArray();

Console.WriteLine("Rescaled so semantic matches what ships, and rounded for a variant string:");
Console.WriteLine();
var overrides = new List<string>();
for (var f = 0; f < featureCount; f++)
{
    var value = Math.Round(scaled[f], 2);
    var key = names[f] == "taste" ? "tasteweight" : $"w{names[f]}";
    overrides.Add($"{key}={value.ToString("0.##", CultureInfo.InvariantCulture)}");
    Console.WriteLine(
        $"  {names[f],-9} {shipped[names[f]],6:F2} -> {value,6:F2}"
        + (Math.Abs(value - shipped[names[f]]) < 0.01 ? "   (unchanged)" : string.Empty));
}

Console.WriteLine();
Console.WriteLine("Test it, do not ship it:");
Console.WriteLine($"  fit:{string.Join(",", overrides)}");
Console.WriteLine();
Console.WriteLine("  Run that variant against mu-human, reco and a held-out library, and read");
Console.WriteLine("  eval-compare.py's interval. A fit on one label set is a hypothesis.");
return 0;

// -------------------------------------------------------------------------------------------------

/// <summary>
/// AdaGrad on the pairwise logistic loss. AdaGrad rather than plain SGD because the channels are on
/// wildly different scales - a cosine against a boolean against a max-normalized graph score - and a
/// single learning rate either crawls on one or diverges on another.
/// </summary>
double[] Fit(bool includePop)
{
    var width = includePop ? featureCount + 1 : featureCount;
    var w = new double[width];
    var accumulated = new double[width];
    // Start from what ships, so a fit that finds nothing returns what was already believed rather
    // than an unrelated vector that happens to score the same.
    for (var f = 0; f < featureCount; f++)
    {
        w[f] = baseline[f];
    }

    var rng = new Random(seed);
    for (var epoch = 0; epoch < epochs; epoch++)
    {
        foreach (var (_, bucket) in usable)
        {
            var pairs = Math.Min(pairsPerRequest, bucket.Positive.Count * bucket.Negative.Count);
            for (var p = 0; p < pairs; p++)
            {
                var positive = bucket.Positive[rng.Next(bucket.Positive.Count)];
                var negative = bucket.Negative[rng.Next(bucket.Negative.Count)];

                var margin = 0.0;
                for (var f = 0; f < width; f++)
                {
                    var index = f == featureCount ? PopColumn : f;
                    margin += w[f] * (positive[index] - negative[index]);
                }

                // d/dw of -log(sigmoid(margin)) is -(1 - sigmoid(margin)) * dx.
                var gradientScale = 1.0 / (1.0 + Math.Exp(margin));
                for (var f = 0; f < width; f++)
                {
                    var index = f == featureCount ? PopColumn : f;
                    var gradient = (-gradientScale * (positive[index] - negative[index])) + (l2 * w[f]);
                    accumulated[f] += gradient * gradient;
                    w[f] -= learningRate * gradient / (Math.Sqrt(accumulated[f]) + 1e-8);
                }
            }
        }
    }

    return w;
}

/// <summary>
/// Share of within-request (relevant, not-relevant) pairs the weights order correctly. Not nDCG, and
/// not a substitute for it: it is what the fit optimizes, so it is the number that says whether the
/// optimizer worked rather than whether the result is any good.
/// </summary>
double PairAccuracy(double[] w, bool includePop)
{
    var width = includePop ? featureCount + 1 : featureCount;
    var correct = 0L;
    var total = 0L;
    foreach (var (_, bucket) in usable)
    {
        foreach (var positive in bucket.Positive)
        {
            foreach (var negative in bucket.Negative)
            {
                var margin = 0.0;
                for (var f = 0; f < width; f++)
                {
                    var index = f == featureCount ? PopColumn : f;
                    margin += w[f] * (positive[index] - negative[index]);
                }

                if (margin > 0)
                {
                    correct++;
                }

                total++;
            }
        }
    }

    return total == 0 ? 0 : (double)correct / total;
}

void Report(string title, double[] w, bool includePop)
{
    Console.WriteLine(title);
    Console.WriteLine(new string('-', title.Length));
    Console.WriteLine($"  pairwise accuracy: {PairAccuracy(w, includePop):P2}");
    for (var f = 0; f < names.Length; f++)
    {
        Console.WriteLine($"  {names[f],-9} {w[f],8:F4}");
    }

    if (includePop)
    {
        // Negative means the fit prefers a LOWER percentile, i.e. a more popular row. That is the
        // fame it is being watched for.
        Console.WriteLine($"  {"pop",-9} {w[featureCount],8:F4}   (negative = prefers famous)");
    }
}
