#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

// Builds the co-read matrix: which series are actually finished by the same people, derived from
// public AniList reading lists.
//
// Run, in order:
//   dotnet run distribution/fetch-coread-graph.cs -- sample --users 10000
//   dotnet run distribution/fetch-coread-graph.cs -- fetch
//   dotnet run distribution/fetch-coread-graph.cs -- build
//   dotnet run distribution/fetch-coread-graph.cs -- export
//   dotnet run distribution/fetch-coread-graph.cs -- stats
//
// WHY THIS EXISTS NEXT TO fetch-reco-graph.cs
// That tool collects the recommendations people wrote. This one collects what they read. Both end
// as an item-item matrix over MangaBaka ids, and they are not the same signal: a recommendation
// exists only where somebody bothered to write one, while a list entry exists for everything anyone
// finished. Measured on 25 sampled users: 80,993 distinct pairs across 845 series, against the
// whole recommendation artifact's 113,243 pairs across 37,493 series built from ~50,000 page
// fetches. Twenty-five requests reached roughly 72% of the pair count that fifty thousand did.
//
// They ship as separate artifacts and separate channels on purpose. One is curated, sparse and
// high-precision; the other is behavioural, dense and noisy. Folding them into one table would put
// a hundred thousand curated pairs next to millions of behavioural ones and lose the smaller
// entirely, and no calibration exists to weigh a written recommendation against a co-completion.
//
// THE WALK
// AniList has no "list every user" endpoint, but it does not need one:
//   1. sample — Page.mediaList(mediaId:) gives up to 5,000 users per seed title, 50 per request,
//      so users are discovered *through* titles rather than by sweeping ids. Seeds are drawn across
//      popularity bands, because seeding only from famous titles collects only the people who read
//      famous titles and the tail never appears.
//   2. fetch — MediaListCollection(userId:) returns that user's entire manga list in ONE request,
//      scores included. This is what makes the whole approach cheap.
//   3. build — turn the lists into an item-item matrix locally.
//
// PRIVACY
// Reading lists are personal data even when public, so: the API only ever returns lists their owner
// made public, the raw rows stay in the working database on this machine, and **only the derived
// item-item matrix is ever exported or published**. `export` writes MangaBaka ids and aggregate
// counts, never a user id. Nothing here identifies a person, and `user_entry` must never be shipped.
//
// THE MATH, AND WHY IT IS NOT A COUNT
// Raw co-occurrence is a popularity chart with extra steps: nearly everyone has finished One Piece,
// so it co-occurs with everything and would dominate every row. Strength is therefore a smoothed
// cosine over the user sets,
//
//     strength(i,j) = weighted_cooccurrence(i,j) / sqrt((users(i) + k) * (users(j) + k))
//
// which is the same shape the recommendation graph's degree penalty was groping towards, done
// properly. The smoothing constant k is doing the job DegreeSmoothing does there: without it a
// series finished by three people, two of whom also finished some other obscurity, scores a perfect
// 1.0 and outranks every real relationship. Two more guards on top: a pair needs `--min-support`
// distinct users before it is emitted at all, and each user's contribution is divided by
// log(1 + their list size), so somebody who has finished nine hundred series does not outvote a
// hundred ordinary readers.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var configDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maki");

// Build output goes to .artifacts, not the config directory. These are build products of a
// distribution tool, git-ignored and hand-published, in the same place build-embeddings.cs and
// eval-*.cs already put theirs; writing them beside the live database would put a multi-hour
// resumable working file somewhere BackupService and the installer are both looking.
var artifactsDir = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts");
Directory.CreateDirectory(artifactsDir);

// Cloudflare 1010-blocks a request with no User-Agent, or with a library's default one, before it
// ever reaches AniList. Costs an hour to discover and one line to avoid.
const string UserAgent = "Maki-coread-graph/1.0 (+https://github.com/OrbitMPGH/Maki)";

var mode = "stats";
var dumpPath = Path.Combine(configDir, "mangabaka.db");
var workPath = Path.Combine(artifactsDir, "coread-graph.db");
var exportPath = Path.Combine(artifactsDir, "coread-edges.db");
var targetUsers = 10_000;
var seedCount = 400;
var rpm = 25;
var batchSize = 20;          // user lists per GraphQL request, via aliases
var maxRequests = 0;
var workPathGiven = false;

// Build knobs. Every one of these exists because the unguarded version of this calculation is a
// popularity chart or a list of obscurities; see the header.
var minSupport = 3;          // distinct users who finished both, below which a pair is noise
var minItemUsers = 3;        // a series nobody much has finished cannot inform anything
var maxItemsPerUser = 200;   // one 900-title list would otherwise contribute 400k pairs
var smoothing = 10.0;        // the k above
var topPerItem = 60;         // neighbours kept per series, so the artifact stays shippable

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "sample" or "fetch" or "build" or "export" or "stats":
            mode = args[i];
            break;
        case "--dump":
            dumpPath = Path.GetFullPath(args[++i]);
            break;
        case "--work":
            workPath = Path.GetFullPath(args[++i]);
            workPathGiven = true;
            break;
        case "--out-db":
            exportPath = Path.GetFullPath(args[++i]);
            break;
        case "--users":
            targetUsers = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--seeds":
            seedCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--rpm":
            rpm = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--batch":
            batchSize = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--max-requests":
            maxRequests = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--min-support":
            minSupport = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--min-item-users":
            minItemUsers = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--max-items":
            maxItemsPerUser = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--smoothing":
            smoothing = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--top":
            topPerItem = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            Console.WriteLine($"error: unknown argument '{args[i]}'");
            return 2;
    }
}

// Measured against the live API: a 20-alias request and a one-field request cost the same single
// unit of the 30/min budget, so batching multiplies throughput at no extra rate-limit risk. Capped
// at 25, where a response's own latency starts exceeding the pacer's spacing anyway.
batchSize = Math.Clamp(batchSize, 1, 25);

// These files used to live in the config directory. A run that resumes state is worthless if it
// silently starts from nothing, so a leftover there is an error with the fix in it rather than a
// new empty database and a lost multi-hour fetch. Delete this once no install has the old layout.
if (!workPathGiven && !File.Exists(workPath) && File.Exists(Path.Combine(configDir, "coread-graph.db")))
{
    Console.WriteLine($"error: no coread-graph.db in {artifactsDir}, but one exists in {configDir}.");
    Console.WriteLine("       These moved to .artifacts. Move it across (with any -wal/-shm sidecars)");
    Console.WriteLine("       or pass the old path explicitly; starting fresh would discard that run.");
    return 2;
}

Console.WriteLine($"config   : {configDir}");
Console.WriteLine($"artifacts: {artifactsDir}");
Console.WriteLine($"work     : {workPath}");
Console.WriteLine($"mode     : {mode}");
Console.WriteLine();

using var work = Work.Open(workPath);

return mode switch
{
    "sample" => await Sample(),
    "fetch" => await Fetch(),
    "build" => Build(),
    "export" => Export(),
    _ => Stats(),
};

// -------------------------------------------------------------------------------------------------
// sample - discover users through titles
// -------------------------------------------------------------------------------------------------
async Task<int> Sample()
{
    if (!File.Exists(dumpPath))
    {
        Console.WriteLine($"error: no MangaBaka dump at {dumpPath}");
        return 2;
    }

    var seeds = Seeds.Pick(dumpPath, seedCount, work.SampledSeeds());
    Console.WriteLine($"seeds  : {seeds.Count} titles across the popularity range");

    var known = work.PendingCount() + work.FetchedCount();
    Console.WriteLine($"users  : {known} known, aiming for {targetUsers}");
    Console.WriteLine();

    if (known >= targetUsers)
    {
        Console.WriteLine("nothing to do: already have enough users. Run `fetch` next.");
        return 0;
    }

    using var cts = Interrupt();
    using var http = Client();
    var pacer = new Pacer(rpm);
    var progress = new Progress(targetUsers - known, "users");
    var added = 0;
    var requests = 0;
    var exhausted = new HashSet<int>();

    // Round-robin across seeds rather than draining one at a time: 5,000 users deep into a single
    // title is 5,000 people who like that title, which is a narrower sample than 50 users each from
    // a hundred different ones.
    for (var page = 1; page <= 100 && !cts.IsCancellationRequested; page++)
    {
        if (exhausted.Count == seeds.Count)
        {
            Console.WriteLine($"{Environment.NewLine}stopping: every seed title ran out of users. Raise --seeds for more.");
            break;
        }

        foreach (var seed in seeds)
        {
            if (exhausted.Contains(seed))
            {
                continue;
            }

            if (cts.IsCancellationRequested || work.PendingCount() + work.FetchedCount() >= targetUsers)
            {
                goto done;
            }

            if (maxRequests > 0 && requests >= maxRequests)
            {
                Console.WriteLine($"{Environment.NewLine}stopping: --max-requests {maxRequests} reached");
                goto done;
            }

            if (work.SeedPageDone(seed, page))
            {
                continue;
            }

            var users = await SampleSeedPage(http, pacer, seed, page, cts.Token);
            requests++;
            if (users is null)
            {
                continue;
            }

            added += work.AddPendingUsers(users, seed, page);
            progress.Advance(work.PendingCount() + work.FetchedCount() - known, absolute: true);

            if (users.Count == 0)
            {
                // This title has no more readers to give. Drop it and carry on with the rest -
                // breaking here would abandon every seed after it in the round for this page too,
                // and since the seed order is stable it would do so on every following page as
                // well, which walks the page counter to 100 and quits far short of the target.
                exhausted.Add(seed);
                continue;
            }
        }
    }

done:
    Console.WriteLine();
    Console.WriteLine($"added {added} new users; {work.PendingCount()} now waiting to be fetched");
    Console.WriteLine("next: dotnet run distribution/fetch-coread-graph.cs -- fetch");
    return cts.IsCancellationRequested ? 130 : 0;
}

async Task<List<int>?> SampleSeedPage(HttpClient http, Pacer pacer, int mediaId, int page, CancellationToken ct)
{
    // COMPLETED only. Someone who has a title on "planning" has not read it, and treating an
    // intention as evidence of taste is how a co-read matrix fills up with whatever is trending.
    const string Query = """
        query ($m: Int, $p: Int) {
          Page(page: $p, perPage: 50) {
            pageInfo { hasNextPage }
            mediaList(mediaId: $m, status_in: [COMPLETED]) { userId }
          }
        }
        """;

    var body = await Post(http, pacer, Query, $"{{\"m\":{mediaId},\"p\":{page}}}", ct);
    if (body is null)
    {
        return null;
    }

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("data", out var data)
        || !data.TryGetProperty("Page", out var pageEl)
        || !pageEl.TryGetProperty("mediaList", out var list)
        || list.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    var users = new List<int>();
    foreach (var entry in list.EnumerateArray())
    {
        if (entry.TryGetProperty("userId", out var idEl) && idEl.TryGetInt32(out var userId))
        {
            users.Add(userId);
        }
    }

    return users;
}

// -------------------------------------------------------------------------------------------------
// fetch - whole lists, --batch users per request
// -------------------------------------------------------------------------------------------------
async Task<int> Fetch()
{
    var pending = work.PendingUsers();
    Console.WriteLine($"pending: {pending.Count} users ({work.FetchedCount()} already fetched)");
    if (pending.Count == 0)
    {
        Console.WriteLine("nothing to do. Run `sample` first, or `build` if fetching is finished.");
        return 0;
    }

    Console.WriteLine();
    using var cts = Interrupt();
    using var http = Client();
    var pacer = new Pacer(rpm);
    var progress = new Progress(pending.Count, "users");
    var totals = new FetchTotals();
    var requests = 0;
    var errorStreak = 0;

    for (var offset = 0; offset < pending.Count; offset += batchSize)
    {
        if (cts.IsCancellationRequested)
        {
            break;
        }

        if (maxRequests > 0 && requests >= maxRequests)
        {
            Console.WriteLine($"{Environment.NewLine}stopping: --max-requests {maxRequests} reached");
            break;
        }

        var batch = pending.GetRange(offset, Math.Min(batchSize, pending.Count - offset));
        var outcome = await FetchUserLists(http, pacer, batch, cts.Token);
        requests += outcome.Requests;

        // Recorded one user at a time, each in its own transaction. Committing the whole batch at
        // once would fsync less, but the write is microseconds beside a multi-second request, and
        // per-user commits mean an interrupt can never dequeue a user whose entries did not land.
        for (var i = 0; i < batch.Count; i++)
        {
            work.RecordUser(batch[i], outcome.Lists[i], totals);
        }

        progress.Advance(batch.Count);

        // The streak counts consecutive failed *requests*, not failed users. Counting users would
        // let one unreachable moment at a batch of 20 blow straight past the limit and abort a run
        // that needed to retry once.
        errorStreak = outcome.Lists.All(l => l.Failure) ? errorStreak + 1 : 0;
        if (errorStreak >= FetchTotals.ErrorStreakLimit)
        {
            Console.WriteLine($"{Environment.NewLine}aborting: {errorStreak} consecutive failed requests - AniList looks unreachable, rerun later");
            break;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{totals.Ok} lists stored, {totals.Private} private or empty, {totals.Errors} errored");
    Console.WriteLine($"{totals.Entries} entries written");
    Console.WriteLine("next: dotnet run distribution/fetch-coread-graph.cs -- build");
    return cts.IsCancellationRequested ? 130 : 0;
}

/// <summary>
/// One request carrying every user in <paramref name="batch"/> as its own GraphQL alias.
///
/// <para>
/// AniList charges its budget per request, not per field: measured against the live API, a 20-alias
/// query and a one-field query each consume exactly one unit of the same 30/min allowance. So the
/// alias form multiplies how many lists an hour of that allowance buys.
/// </para>
///
/// <para>
/// The cost is that one private user takes the whole request down with it, which the recovery below
/// exists to undo. Returns results aligned to <paramref name="batch"/>, and the number of requests
/// it actually took.
/// </para>
/// </summary>
async Task<BatchOutcome> FetchUserLists(
    HttpClient http, Pacer pacer, List<int> batch, CancellationToken ct)
{
    // Each alias's column is recorded as the query is built, because that is how a failing alias
    // gets named: see BlameAliases.
    var columns = new int[batch.Count];
    var query = new StringBuilder("{");
    for (var i = 0; i < batch.Count; i++)
    {
        columns[i] = query.Length + 2; // 1-based column of the 'u' that opens this alias
        query.Append(CultureInfo.InvariantCulture,
            $" u{i}: MediaListCollection(userId: {batch[i]}, type: MANGA) {{ lists {{ entries {{ mediaId score(format: POINT_100) status }} }} }}");
    }

    query.Append(" }");

    var body = await Post(http, pacer, query.ToString(), "{}", ct);
    var requests = 1;

    if (TryReadAll(body, batch.Count) is { } resolved)
    {
        return new BatchOutcome(resolved, requests);
    }

    // Down to one user the ambiguity is gone: a null collection really is that user, and "private
    // or deleted" is settled rather than retryable. Anything else did not get through.
    if (batch.Count == 1)
    {
        return new BatchOutcome([SingleOutcome(body)], requests);
    }

    if (ct.IsCancellationRequested)
    {
        return new BatchOutcome([.. batch.Select(_ => UserList.Failed)], requests);
    }

    var blamed = BlameAliases(body, columns);
    if (blamed.Count == 0)
    {
        // Nothing nameable went wrong, so the request itself did not answer. Halving isolates a
        // problem that has no error to point at, and terminates at a batch of one.
        var mid = batch.Count / 2;
        var left = await FetchUserLists(http, pacer, batch[..mid], ct);
        var right = await FetchUserLists(http, pacer, batch[mid..], ct);
        return new BatchOutcome(
            [.. left.Lists, .. right.Lists], requests + left.Requests + right.Requests);
    }

    var results = new UserList[batch.Count];
    var survivors = new List<int>(batch.Count);
    var survivorSlots = new List<int>(batch.Count);
    for (var i = 0; i < batch.Count; i++)
    {
        if (blamed.Contains(i))
        {
            results[i] = UserList.Unavailable;
        }
        else
        {
            survivors.Add(batch[i]);
            survivorSlots.Add(i);
        }
    }

    if (survivors.Count > 0)
    {
        var retry = await FetchUserLists(http, pacer, survivors, ct);
        requests += retry.Requests;
        for (var i = 0; i < survivorSlots.Count; i++)
        {
            results[survivorSlots[i]] = retry.Lists[i];
        }
    }

    return new BatchOutcome([.. results], requests);
}

/// <summary>
/// Names the aliases AniList refused, by matching each error's reported column against the columns
/// recorded while the query was built.
///
/// <para>
/// This is what keeps batching worth doing. A batch holding even one private list comes back HTTP
/// 404 with <em>every</em> alias null, so without a way to name the offender the only recovery is
/// to halve and retry, and at a 3% private rate most batches take that path - measured at twelve
/// requests to resolve twenty users, barely better than not batching at all. GraphQL requires an
/// error to carry the location of the field that produced it, and AniList emits one "Private User"
/// error per offending alias, so the offenders can be marked settled and everyone else refetched in
/// a single further request.
/// </para>
///
/// <para>
/// Only <c>status: 404</c> errors are believed. A different error is not evidence that a user is
/// private, and filing them as such would dequeue somebody permanently over a transient fault; those
/// fall through to halving instead.
/// </para>
/// </summary>
static HashSet<int> BlameAliases(string? body, int[] columns)
{
    var blamed = new HashSet<int>();
    if (body is null)
    {
        return blamed;
    }

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("errors", out var errors)
        || errors.ValueKind != JsonValueKind.Array)
    {
        return blamed;
    }

    foreach (var error in errors.EnumerateArray())
    {
        if (!error.TryGetProperty("status", out var status)
            || !status.TryGetInt32(out var code)
            || code != 404)
        {
            return []; // an unexplained error means the whole mapping is untrustworthy
        }

        if (!error.TryGetProperty("locations", out var locations)
            || locations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var location in locations.EnumerateArray())
        {
            if (!location.TryGetProperty("column", out var col) || !col.TryGetInt32(out var column))
            {
                return [];
            }

            // The reported column sits at or just after the alias it belongs to, so the alias is the
            // last one starting at or before it. Exact equality is what the API actually sends;
            // the search is what keeps this from breaking if that ever shifts by a character.
            var index = -1;
            for (var i = 0; i < columns.Length; i++)
            {
                if (columns[i] <= column)
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                return [];
            }

            blamed.Add(index);
        }
    }

    return blamed;
}

/// <summary>
/// Reads every alias, or returns null to say the response cannot be trusted for this batch.
///
/// <para>
/// <b>A null alias in a multi-user response says nothing about that user.</b> AniList answers a
/// batch containing even one private list with HTTP 404 and every alias null, not just the offending
/// one - measured: a user with 356 entries came back null purely for sharing a request with a
/// private account. Trusting those nulls files the whole batch as "private", which is settled and
/// never retried, so nineteen good lists are lost per private one and the run quietly collects a
/// fraction of what it should.
/// </para>
///
/// <para>
/// So a null is never believed here. An empty-but-public list is a different thing and is not a
/// null: the collection is present with no entries, and stays settled.
/// </para>
/// </summary>
static List<UserList>? TryReadAll(string? body, int count)
{
    if (body is null)
    {
        return null;
    }

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
    {
        return null;
    }

    var lists = new List<UserList>(count);
    for (var i = 0; i < count; i++)
    {
        if (!data.TryGetProperty($"u{i}", out var alias) || alias.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        lists.Add(ReadUserList(alias));
    }

    return lists;
}

/// <summary>
/// The verdict on a batch of one, where a null collection is no longer ambiguous. A response that
/// parsed and carried a <c>data</c> object means AniList answered and the user is private or
/// deleted: settled, never retried. Anything else did not get through.
/// </summary>
static UserList SingleOutcome(string? body)
{
    if (body is null)
    {
        return UserList.Failed;
    }

    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.TryGetProperty("data", out _) ? UserList.Unavailable : UserList.Failed;
}

/// <summary>
/// Reads one alias's collection. A null or absent collection is a private or deleted user, which is
/// settled and must never be retried: the answer will be the same tomorrow.
/// </summary>
static UserList ReadUserList(JsonElement collection)
{
    if (collection.ValueKind != JsonValueKind.Object
        || !collection.TryGetProperty("lists", out var lists)
        || lists.ValueKind != JsonValueKind.Array)
    {
        return UserList.Unavailable;
    }

    var entries = new List<(int MediaId, int Score, string Status)>();
    foreach (var list in lists.EnumerateArray())
    {
        if (!list.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            continue;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("mediaId", out var idEl) || !idEl.TryGetInt32(out var mediaId))
            {
                continue;
            }

            var score = row.TryGetProperty("score", out var s) && s.TryGetInt32(out var v) ? v : 0;
            var status = row.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            entries.Add((mediaId, score, status));
        }
    }

    return entries.Count == 0 ? UserList.Unavailable : UserList.Ok(entries);
}

// -------------------------------------------------------------------------------------------------
// build - lists into an item-item matrix
// -------------------------------------------------------------------------------------------------
int Build()
{
    if (!File.Exists(dumpPath))
    {
        Console.WriteLine($"error: no MangaBaka dump at {dumpPath}");
        return 2;
    }

    var clock = Stopwatch.StartNew();
    var crossRef = Seeds.CrossReference(dumpPath);
    Console.WriteLine($"cross-ref: {crossRef.Count} AniList ids map to MangaBaka series");

    // Only COMPLETED counts, and only series the dump knows: an edge to an id the rest of Maki
    // cannot resolve is dead weight in the index.
    var byUser = work.CompletedByUser(crossRef);
    Console.WriteLine($"lists    : {byUser.Count} users with at least one resolvable completed series");
    if (byUser.Count == 0)
    {
        Console.WriteLine("nothing to build yet.");
        return 1;
    }

    var frequency = new Dictionary<long, int>();
    foreach (var items in byUser.Values)
    {
        foreach (var id in items)
        {
            frequency[id] = frequency.GetValueOrDefault(id) + 1;
        }
    }

    var keep = frequency.Where(f => f.Value >= minItemUsers).Select(f => f.Key).ToHashSet();
    Console.WriteLine($"series   : {frequency.Count} seen, {keep.Count} finished by at least {minItemUsers} users");

    // Dense indices, so a pair key packs into one long and the accumulator stays a flat dictionary
    // rather than a dictionary of tuples.
    var index = keep.ToArray();
    Array.Sort(index);
    var slot = new Dictionary<long, int>(index.Length);
    for (var i = 0; i < index.Length; i++)
    {
        slot[index[i]] = i;
    }

    // Each user's contribution, resolved once: their dense slots (sorted, capped) and the weight
    // every one of their pairs carries. Doing this inside the pair loop meant two lookups into
    // `slot` per pair, which is 178 million lookups nobody needed.
    var contributors = new List<(int[] Slots, double Contribution)>(byUser.Count);
    var increments = 0L;

    foreach (var items in byUser.Values)
    {
        var mine = items.Where(keep.Contains).Distinct().ToArray();
        if (mine.Length < 2)
        {
            continue;
        }

        // Inverse user frequency: a completionist's every pair is weaker evidence than a pair from
        // somebody who finished twelve things, because the completionist would pair anything with
        // anything. Applied before the cap so the weight reflects the real list size.
        var contribution = 1.0 / Math.Log(1 + mine.Length);

        if (mine.Length > maxItemsPerUser)
        {
            // Deterministic, and not simply the first N by id: taking a stable pseudo-random slice
            // keyed by the ids themselves avoids systematically favouring low MangaBaka ids, which
            // correlate with older titles.
            Array.Sort(mine, (x, y) => Mix(x).CompareTo(Mix(y)));
            mine = mine[..maxItemsPerUser];
        }

        var slots = new int[mine.Length];
        for (var i = 0; i < mine.Length; i++)
        {
            slots[i] = slot[mine[i]];
        }

        Array.Sort(slots);
        contributors.Add((slots, contribution));
        increments += (long)mine.Length * (mine.Length - 1) / 2;
    }

    var users = contributors.Count;

    // PARTITIONED, BECAUSE THE WHOLE ACCUMULATOR DOES NOT FIT ANYWHERE SENSIBLE
    // At 8,800 users this loop generates ~89 million pair increments over ~40 million distinct
    // pairs, and 76% of those are seen exactly once and dropped by minSupport moments later. Held
    // in one dictionary that is several GB of random-access hash table: every increment is a cache
    // and TLB miss, and the table doubles itself twenty-odd times on the way up, rehashing
    // everything each time.
    //
    // So the pass is run once per hash partition, keeping only the keys belonging to that
    // partition. Memory is bounded by PairsPerPass regardless of how many users are fetched; the
    // cost is re-walking a very cheap nested loop K times, which is seconds. Partitioning on the
    // key's own bits (not a re-hash) is enough here: the key is two dense indices, so the low bits
    // are uniform by construction.
    const int PairsPerPass = 12_000_000;
    // A power of two so the partition test is a mask rather than a modulo on the hot path.
    var passes = (int)BitOperations.RoundUpToPowerOf2(
        (uint)Math.Clamp((increments + PairsPerPass - 1) / PairsPerPass, 1, 64));
    var mask = passes - 1;

    Console.WriteLine(
        $"pairs    : {increments} increments from {users} users, in {passes} pass(es)");

    var rows = new List<(long A, long B, int Support, double Strength)>();
    var distinct = 0L;

    for (var pass = 0; pass < passes; pass++)
    {
        // Sized for its share up front: the resize-and-rehash cycle is most of the cost otherwise.
        var counts = new Dictionary<long, PairStat>(
            (int)Math.Min(PairsPerPass, increments / passes + 16));

        foreach (var (slots, contribution) in contributors)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                var high = (long)slots[i] << 32;
                for (var j = i + 1; j < slots.Length; j++)
                {
                    var key = high | (uint)slots[j];
                    if ((key & mask) != pass)
                    {
                        continue;
                    }

                    // One hash lookup per pair rather than four: the ref is into the entry itself,
                    // so support and weight are updated in place.
                    ref var stat = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, key, out _);
                    stat.Support++;
                    stat.Weight += contribution;
                }
            }
        }

        distinct += counts.Count;

        foreach (var (key, stat) in counts)
        {
            if (stat.Support < minSupport)
            {
                continue;
            }

            var a = index[(int)(key >> 32)];
            var b = index[(int)(uint)key];
            var strength = stat.Weight /
                Math.Sqrt((frequency[a] + smoothing) * (frequency[b] + smoothing));
            rows.Add((a, b, stat.Support, strength));
        }

        Console.WriteLine(
            $"  pass {pass + 1}/{passes}: {counts.Count} distinct, {rows.Count} kept so far, {clock.Elapsed.TotalSeconds:F0}s");
    }

    Console.WriteLine($"distinct : {distinct} pairs seen");
    Console.WriteLine($"kept     : {rows.Count} pairs with support >= {minSupport}");

    // Truncate per series rather than globally: a global cut keeps only pairs among popular titles
    // and empties the tail, which is the half the embeddings are already worst at.
    var perItem = new Dictionary<long, List<int>>();
    for (var i = 0; i < rows.Count; i++)
    {
        perItem.TryAdd(rows[i].A, []);
        perItem.TryAdd(rows[i].B, []);
        perItem[rows[i].A].Add(i);
        perItem[rows[i].B].Add(i);
    }

    var survivors = new HashSet<int>();
    foreach (var owned in perItem.Values)
    {
        // Sorted in place rather than through OrderByDescending: this runs once per series, and the
        // LINQ form allocates an iterator, a buffer and a key array for every one of them.
        if (owned.Count > topPerItem)
        {
            owned.Sort((x, y) => rows[y].Strength.CompareTo(rows[x].Strength));
        }

        for (var i = 0; i < owned.Count && i < topPerItem; i++)
        {
            survivors.Add(owned[i]);
        }
    }

    Console.WriteLine($"top {topPerItem}   : {survivors.Count} pairs survive per-series truncation");

    Console.WriteLine($"writing  : {survivors.Count} rows …");
    work.WriteMatrix(survivors.Select(i => rows[i]), users);
    Console.WriteLine($"done in {clock.Elapsed.TotalSeconds:F0}s. Next: export");
    return 0;

    // Cheap integer hash, purely to shuffle a user's items deterministically before capping.
    static long Mix(long v)
    {
        var x = (ulong)v * 0x9E3779B97F4A7C15UL;
        x ^= x >> 29;
        return (long)(x & 0x7FFFFFFFFFFFFFFF);
    }
}

// -------------------------------------------------------------------------------------------------
// export - the shippable artifact, with no user in it
// -------------------------------------------------------------------------------------------------
int Export()
{
    var count = work.MatrixCount();
    if (count == 0)
    {
        Console.WriteLine("nothing to export: run `build` first.");
        return 1;
    }

    if (File.Exists(exportPath))
    {
        File.Delete(exportPath);
    }

    using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = exportPath }.ToString());
    conn.Open();
    Exec(conn, "PRAGMA journal_mode=OFF;");
    Exec(conn, "PRAGMA synchronous=OFF;");
    Exec(conn, """
        CREATE TABLE pair (
            a_id     INTEGER NOT NULL,
            b_id     INTEGER NOT NULL,
            support  INTEGER NOT NULL,
            strength REAL    NOT NULL,
            PRIMARY KEY (a_id, b_id)
        ) WITHOUT ROWID;
        """);

    using (var attach = conn.CreateCommand())
    {
        attach.CommandText = "ATTACH DATABASE $src AS w";
        attach.Parameters.AddWithValue("$src", workPath);
        attach.ExecuteNonQuery();
    }

    // Only the aggregate crosses this line. `user_entry` stays on the machine that fetched it.
    Exec(conn, "INSERT INTO pair SELECT a_id, b_id, support, strength FROM w.cooccurrence;");
    Exec(conn, "CREATE INDEX ix_pair_b ON pair (b_id, a_id);");

    Exec(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    using (var meta = conn.CreateCommand())
    {
        meta.CommandText = """
            INSERT INTO meta (key, value) VALUES
                ('schemaVersion', '1'),
                ('generatedAt', $at),
                ('pairCount', (SELECT COUNT(*) FROM pair)),
                ('seriesCount', (SELECT COUNT(*) FROM (SELECT a_id AS id FROM pair UNION SELECT b_id FROM pair))),
                ('userCount', (SELECT value FROM w.meta WHERE key = 'buildUsers')),
                ('source', 'anilist-lists')
            """;
        meta.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        meta.ExecuteNonQuery();
    }

    Exec(conn, "DETACH DATABASE w;");
    Exec(conn, "VACUUM;");

    Console.WriteLine($"exported : {exportPath}");
    Console.WriteLine($"  pairs  : {count}");
    Console.WriteLine($"  size   : {new FileInfo(exportPath).Length / 1024.0 / 1024.0:F1} MB");
    return 0;
}

// -------------------------------------------------------------------------------------------------
// stats
// -------------------------------------------------------------------------------------------------
int Stats()
{
    var (pending, fetched, entries) = work.Counts();
    Console.WriteLine("users");
    Console.WriteLine($"  waiting to fetch : {pending}");
    Console.WriteLine($"  fetched          : {fetched}");
    Console.WriteLine($"  list entries     : {entries}");

    foreach (var (status, n) in work.UserStatuses())
    {
        Console.WriteLine($"    {status,-10} {n}");
    }

    var matrix = work.MatrixCount();
    Console.WriteLine();
    Console.WriteLine("matrix");
    Console.WriteLine($"  pairs  : {matrix}");
    if (matrix > 0)
    {
        var (series, support, strength) = work.MatrixShape();
        Console.WriteLine($"  series : {series}");
        Console.WriteLine($"  support: median {support}");
        Console.WriteLine($"  strength: max {strength:F4}");
    }

    return 0;
}

static void Exec(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

CancellationTokenSource Interrupt()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("interrupt: finishing the current step, then stopping (rerun to resume)");
        cts.Cancel();
    };
    return cts;
}

static HttpClient Client()
{
    var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    http.DefaultRequestHeaders.Add("Accept", "application/json");
    return http;
}

async Task<string?> Post(HttpClient http, Pacer pacer, string query, string variables, CancellationToken ct)
{
    var payload = new MemoryStream();
    using (var writer = new Utf8JsonWriter(payload))
    {
        writer.WriteStartObject();
        writer.WriteString("query", query);
        writer.WritePropertyName("variables");
        writer.WriteRawValue(variables);
        writer.WriteEndObject();
    }

    var bytes = payload.ToArray();

    for (var attempt = 0; attempt < 5 && !ct.IsCancellationRequested; attempt++)
    {
        await pacer.WaitAsync(ct);

        HttpResponseMessage response;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
            {
                Content = new ByteArrayContent(bytes),
            };
            request.Content.Headers.ContentType = new("application/json");
            response = await http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Environment.NewLine}warn: {ex.GetType().Name} - retrying in {1 << attempt}s");
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            continue;
        }

        using (response)
        {
            pacer.Observe(response);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.FromSeconds(60);
                }

                pacer.Throttle();
                Console.WriteLine($"{Environment.NewLine}rate limited: sleeping {wait.TotalSeconds:F0}s, target now {pacer.Rpm} req/min");
                await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
                continue;
            }

            // 404 is AniList's answer when any user named in the request is private or deleted -
            // for a batch, that is the whole request, not one alias. The body still parses, so it
            // is handed back for the caller to decide; FetchUserLists splits rather than believing
            // the nulls it contains.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return await response.Content.ReadAsStringAsync(ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"{Environment.NewLine}warn: HTTP {(int)response.StatusCode} - retrying in {1 << attempt}s");
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
                continue;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    return null;
}

// -------------------------------------------------------------------------------------------------
// types
// -------------------------------------------------------------------------------------------------

/// <summary>One user's list, or the reason there isn't one.</summary>
internal sealed class UserList
{
    public List<(int MediaId, int Score, string Status)> Entries { get; private init; } = [];

    /// <summary>Settled: private, deleted, or genuinely empty. Never retried.</summary>
    public bool Unavailableness { get; private init; }

    /// <summary>Did not get through. Retried on a later run.</summary>
    public bool Failure { get; private init; }

    public static UserList Ok(List<(int, int, string)> entries) => new() { Entries = entries };

    public static UserList Unavailable => new() { Unavailableness = true };

    public static UserList Failed => new() { Failure = true };
}

/// <summary>
/// One pair's running totals. A struct in one dictionary rather than two parallel dictionaries:
/// halves the entries, and lets <c>GetValueRefOrAddDefault</c> update both fields from a single
/// hash lookup instead of four.
/// </summary>
internal struct PairStat
{
    public int Support;
    public double Weight;
}

/// <summary>One request's results, aligned to the batch it was built from, plus what it cost.</summary>
internal readonly record struct BatchOutcome(List<UserList> Lists, int Requests);

internal sealed class FetchTotals
{
    public const int ErrorStreakLimit = 12;

    public int Ok;
    public int Private;
    public int Errors;
    public int Entries;
}

/// <summary>Seed selection and the AniList-to-MangaBaka mapping, both read from the dump.</summary>
internal static class Seeds
{
    private const int Unranked = int.MaxValue;

    /// <summary>
    /// Seed titles spread across the popularity range, not simply the most popular ones. Sampling
    /// users through famous titles alone collects people who read famous titles, and the tail this
    /// matrix is supposed to illuminate never shows up in anybody's list.
    /// </summary>
    public static List<int> Pick(string dumpPath, int count, HashSet<int> exclude)
    {
        var ranked = new List<(int AniListId, int Popularity)>();
        using var conn = OpenReadOnly(dumpPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_anilist_id, popularity_global_current
            FROM series
            WHERE state = 'active' AND type != 'novel' AND source_anilist_id IS NOT NULL
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ranked.Add((
                (int)reader.GetInt64(0),
                reader.IsDBNull(1) ? Unranked : (int)reader.GetInt64(1)));
        }

        // Popularity order, then an even stride through it: the top of the list, the middle and the
        // thinly-read tail all contribute seeds.
        ranked.Sort((a, b) => a.Popularity.CompareTo(b.Popularity));

        // Below this a title has too few readers for a page of users to come back, and the request
        // is wasted. Generous, because "obscure" is the point.
        var pool = ranked.Take(60_000).Select(r => r.AniListId).Where(id => !exclude.Contains(id)).ToList();
        if (pool.Count <= count)
        {
            return pool;
        }

        var stride = (double)pool.Count / count;
        var picked = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            picked.Add(pool[(int)(i * stride)]);
        }

        return picked;
    }

    /// <summary>AniList id to MangaBaka id, for the series the dump can actually resolve.</summary>
    public static Dictionary<long, long> CrossReference(string dumpPath)
    {
        var map = new Dictionary<long, long>(150_000);
        using var conn = OpenReadOnly(dumpPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_anilist_id, id, popularity_global_current
            FROM series
            WHERE state = 'active' AND type != 'novel' AND source_anilist_id IS NOT NULL
            ORDER BY COALESCE(popularity_global_current, 2147483647)
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // Ordered by popularity, so the first row to claim an AniList id wins - the same
            // collision rule fetch-reco-graph.cs uses, for the same reason.
            map.TryAdd(reader.GetInt64(0), reader.GetInt64(1));
        }

        return map;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        return conn;
    }
}

/// <summary>
/// The working database. Holds the raw per-user rows, which are personal data and stay here: only
/// <c>cooccurrence</c> is ever exported.
/// </summary>
internal sealed class Work : IDisposable
{
    private readonly SqliteConnection _conn;

    private Work(SqliteConnection conn) => _conn = conn;

    public static Work Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        Run(conn, "PRAGMA journal_mode=WAL;");
        Run(conn, "PRAGMA synchronous=NORMAL;");
        Run(conn, """
            CREATE TABLE IF NOT EXISTS pending_user (user_id INTEGER PRIMARY KEY, found_via INTEGER NOT NULL);
            """);
        Run(conn, """
            CREATE TABLE IF NOT EXISTS user_state (
                user_id    INTEGER PRIMARY KEY,
                status     TEXT    NOT NULL,
                entries    INTEGER NOT NULL DEFAULT 0,
                fetched_at TEXT    NOT NULL
            );
            """);
        Run(conn, """
            CREATE TABLE IF NOT EXISTS user_entry (
                user_id  INTEGER NOT NULL,
                media_id INTEGER NOT NULL,
                score    INTEGER NOT NULL,
                status   TEXT    NOT NULL,
                PRIMARY KEY (user_id, media_id)
            ) WITHOUT ROWID;
            """);
        Run(conn, """
            CREATE TABLE IF NOT EXISTS seed_page (
                media_id INTEGER NOT NULL,
                page     INTEGER NOT NULL,
                users    INTEGER NOT NULL,
                PRIMARY KEY (media_id, page)
            ) WITHOUT ROWID;
            """);
        Run(conn, """
            CREATE TABLE IF NOT EXISTS cooccurrence (
                a_id     INTEGER NOT NULL,
                b_id     INTEGER NOT NULL,
                support  INTEGER NOT NULL,
                strength REAL    NOT NULL,
                PRIMARY KEY (a_id, b_id)
            ) WITHOUT ROWID;
            """);
        Run(conn, "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        return new Work(conn);
    }

    public int PendingCount() => Scalar("SELECT COUNT(*) FROM pending_user");

    public int FetchedCount() => Scalar("SELECT COUNT(*) FROM user_state");

    public HashSet<int> SampledSeeds()
    {
        var set = new HashSet<int>();
        using var cmd = _conn.CreateCommand();

        // A seed whose pages came back empty has nothing more to give; skip it next run. Any
        // page, not just the first: a title with 300 readers empties at page 7 and is just as
        // finished as one that was empty from the start.
        cmd.CommandText = "SELECT media_id FROM seed_page WHERE users = 0";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add((int)reader.GetInt64(0));
        }

        return set;
    }

    public bool SeedPageDone(int mediaId, int page)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM seed_page WHERE media_id = $m AND page = $p";
        cmd.Parameters.AddWithValue("$m", mediaId);
        cmd.Parameters.AddWithValue("$p", page);
        return cmd.ExecuteScalar() is not null;
    }

    public int AddPendingUsers(List<int> users, int seed, int page)
    {
        using var tx = _conn.BeginTransaction();

        using var insert = _conn.CreateCommand();
        insert.Transaction = tx;

        // Already-fetched users are not re-queued: sampling deliberately revisits popular titles,
        // so the same person turns up through many seeds.
        insert.CommandText = """
            INSERT OR IGNORE INTO pending_user (user_id, found_via) VALUES ($u, $s)
            """;
        var up = insert.Parameters.Add("$u", SqliteType.Integer);
        insert.Parameters.AddWithValue("$s", seed);

        var added = 0;
        foreach (var user in users)
        {
            up.Value = user;
            added += insert.ExecuteNonQuery();
        }

        using (var seen = _conn.CreateCommand())
        {
            seen.Transaction = tx;
            seen.CommandText = "INSERT OR REPLACE INTO seed_page (media_id, page, users) VALUES ($m, $p, $n)";
            seen.Parameters.AddWithValue("$m", seed);
            seen.Parameters.AddWithValue("$p", page);
            seen.Parameters.AddWithValue("$n", users.Count);
            seen.ExecuteNonQuery();
        }

        using (var prune = _conn.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = "DELETE FROM pending_user WHERE user_id IN (SELECT user_id FROM user_state)";
            prune.ExecuteNonQuery();
        }

        tx.Commit();
        return added;
    }

    public List<int> PendingUsers()
    {
        var users = new List<int>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM pending_user ORDER BY user_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            users.Add((int)reader.GetInt64(0));
        }

        return users;
    }

    public void RecordUser(int userId, UserList list, FetchTotals totals)
    {
        using var tx = _conn.BeginTransaction();

        var status = list.Failure ? "error" : list.Unavailableness ? "private" : "ok";
        if (list.Failure)
        {
            totals.Errors++;
        }
        else if (list.Unavailableness)
        {
            totals.Private++;
        }
        else
        {
            totals.Ok++;
        }

        if (list.Entries.Count > 0)
        {
            using var insert = _conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR REPLACE INTO user_entry (user_id, media_id, score, status)
                VALUES ($u, $m, $sc, $st)
                """;
            insert.Parameters.AddWithValue("$u", userId);
            var mp = insert.Parameters.Add("$m", SqliteType.Integer);
            var sp = insert.Parameters.Add("$sc", SqliteType.Integer);
            var tp = insert.Parameters.Add("$st", SqliteType.Text);

            foreach (var (mediaId, score, entryStatus) in list.Entries)
            {
                mp.Value = mediaId;
                sp.Value = score;
                tp.Value = entryStatus;
                insert.ExecuteNonQuery();
            }

            totals.Entries += list.Entries.Count;
        }

        // A failed fetch still leaves the row queued, so a later run retries it; anything settled is
        // recorded and dequeued.
        if (!list.Failure)
        {
            using var state = _conn.CreateCommand();
            state.Transaction = tx;
            state.CommandText = """
                INSERT OR REPLACE INTO user_state (user_id, status, entries, fetched_at)
                VALUES ($u, $s, $e, $at)
                """;
            state.Parameters.AddWithValue("$u", userId);
            state.Parameters.AddWithValue("$s", status);
            state.Parameters.AddWithValue("$e", list.Entries.Count);
            state.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            state.ExecuteNonQuery();

            using var dequeue = _conn.CreateCommand();
            dequeue.Transaction = tx;
            dequeue.CommandText = "DELETE FROM pending_user WHERE user_id = $u";
            dequeue.Parameters.AddWithValue("$u", userId);
            dequeue.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Completed series per user, already mapped into MangaBaka ids.</summary>
    public Dictionary<int, List<long>> CompletedByUser(Dictionary<long, long> crossRef)
    {
        var byUser = new Dictionary<int, List<long>>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, media_id FROM user_entry WHERE status = 'COMPLETED'";
        cmd.CommandTimeout = 600;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!crossRef.TryGetValue(reader.GetInt64(1), out var mangaBakaId))
            {
                continue;
            }

            var userId = (int)reader.GetInt64(0);
            if (!byUser.TryGetValue(userId, out var list))
            {
                byUser[userId] = list = [];
            }

            list.Add(mangaBakaId);
        }

        return byUser;
    }

    public void WriteMatrix(IEnumerable<(long A, long B, int Support, double Strength)> rows, int users)
    {
        using var tx = _conn.BeginTransaction();
        Run(_conn, tx, "DELETE FROM cooccurrence;");

        using var insert = _conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT OR REPLACE INTO cooccurrence (a_id, b_id, support, strength)
            VALUES ($a, $b, $s, $w)
            """;
        var ap = insert.Parameters.Add("$a", SqliteType.Integer);
        var bp = insert.Parameters.Add("$b", SqliteType.Integer);
        var sp = insert.Parameters.Add("$s", SqliteType.Integer);
        var wp = insert.Parameters.Add("$w", SqliteType.Real);

        foreach (var (a, b, support, strength) in rows)
        {
            ap.Value = a;
            bp.Value = b;
            sp.Value = support;
            wp.Value = strength;
            insert.ExecuteNonQuery();
        }

        using (var meta = _conn.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                INSERT INTO meta (key, value) VALUES ('buildUsers', $u)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value
                """;
            meta.Parameters.AddWithValue("$u", users.ToString(CultureInfo.InvariantCulture));
            meta.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public int MatrixCount() => Scalar("SELECT COUNT(*) FROM cooccurrence");

    public (int Series, int Support, double Strength) MatrixShape()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM (SELECT a_id AS id FROM cooccurrence UNION SELECT b_id FROM cooccurrence)),
                   (SELECT support FROM cooccurrence ORDER BY support LIMIT 1 OFFSET (SELECT COUNT(*) / 2 FROM cooccurrence)),
                   (SELECT MAX(strength) FROM cooccurrence)
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return ((int)reader.GetInt64(0), reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetDouble(2));
    }

    public (int Pending, int Fetched, int Entries) Counts() =>
        (PendingCount(), FetchedCount(), Scalar("SELECT COUNT(*) FROM user_entry"));

    public List<(string Status, int Count)> UserStatuses()
    {
        var rows = new List<(string, int)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM user_state GROUP BY status ORDER BY status";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), (int)reader.GetInt64(1)));
        }

        return rows;
    }

    private int Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Run(SqliteConnection conn, string sql) => Run(conn, null, sql);

    private static void Run(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}

/// <summary>Request pacing, identical in spirit to fetch-reco-graph.cs's.</summary>
internal sealed class Pacer(int rpm)
{
    private readonly Lock _gate = new();
    private DateTime _next = DateTime.UtcNow;
    private int _rpm = Math.Max(1, rpm);

    public int Rpm => _rpm;

    public async Task WaitAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_gate)
        {
            var spacing = TimeSpan.FromSeconds(60.0 / _rpm);
            var now = DateTime.UtcNow;
            if (_next < now)
            {
                _next = now;
            }

            delay = _next - now;
            _next += spacing;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }
    }

    public void Observe(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Limit", out var values)
            || !int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            || limit <= 0)
        {
            return;
        }

        var target = Math.Max(1, (int)(limit * 0.8));
        lock (_gate)
        {
            if (target < _rpm)
            {
                _rpm = target;
            }
        }
    }

    public void Throttle()
    {
        lock (_gate)
        {
            _rpm = Math.Max(1, _rpm / 2);
        }
    }
}

/// <summary>One rewriting status line with an ETA from measured throughput.</summary>
internal sealed class Progress(int total, string unit)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _done;
    private DateTime _lastPrint = DateTime.MinValue;

    public void Advance(int n, bool absolute = false)
    {
        _done = absolute ? n : _done + n;

        if (_done < total && DateTime.UtcNow - _lastPrint < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        _lastPrint = DateTime.UtcNow;
        var rate = _done / Math.Max(1.0, _clock.Elapsed.TotalSeconds);
        var eta = rate > 0 ? TimeSpan.FromSeconds(Math.Max(0, total - _done) / rate) : TimeSpan.Zero;
        Console.Write($"\r  {_done}/{total} {unit}  {rate * 60:F0}/min  ETA {Format(eta)}      ");
    }

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m" : $"{(int)t.TotalMinutes}m{t.Seconds:D2}s";
}
