#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

// Builds the item-item recommendation graph a collaborative signal needs, WITHOUT scraping a single
// user's list.
//
// Run:
//   dotnet run distribution/fetch-reco-graph.cs
//   dotnet run distribution/fetch-reco-graph.cs -- fetch --provider anilist --top 50000 --pages 2
//   dotnet run distribution/fetch-reco-graph.cs -- fetch --provider mal --top 20000
//   dotnet run distribution/fetch-reco-graph.cs -- stats
//   dotnet run distribution/fetch-reco-graph.cs -- export --out-db reco-edges.db
//
// WHY THIS EXISTS
// SemanticRecommender is entirely content-based: bge embeddings over title/description/tags, plus a
// popularity prior. It has no idea that people who finish A overwhelmingly go on to B unless the two
// happen to describe alike. The usual fix is collaborative filtering over per-user rating matrices,
// which for manga means scraping hundreds of thousands of private-ish user lists. Both big trackers
// already publish the aggregate of exactly that signal - user-submitted "if you liked X, try Y" pairs
// with vote counts - so this tool collects those instead. One request per title rather than one per
// user, no personal data ever stored, and the output is the item-item matrix CF would have had to
// derive anyway.
//
// WHAT IT WRITES
// .artifacts/reco-graph.db, entirely separate from mangabaka.db and embeddings.db. Two real tables:
//   edge        directed (from -> to) in MANGABAKA ids, one row per provider, carrying that
//               provider's vote count. Kept directed and un-merged so a later consumer can decide
//               how to symmetrize and how to weigh the two providers against each other; collapsing
//               that here would throw away information no later pass can recover.
//   fetch_state one row per (provider, remote id) attempted. This is what makes the run resumable:
//               a job spanning ~7 hours WILL be interrupted, and re-listing 127k titles to work out
//               where it stopped is not acceptable. Ctrl-C is safe at any point.
//
// SCALE, MEASURED AGAINST THE INSTALLED DUMP
// 127,890 active non-novel series carry an AniList id, 72,954 a MAL one. AniList batches ~10 titles
// per GraphQL request via aliases, so at 25 req/min that is ~9 hours unattended. MAL has no batch
// form and no API, so it is one page fetch per title: ~20 hours at 30/min. Start with AniList and
// --top, which takes titles in global popularity order and gets you the useful 90% of the graph in
// an evening.
//
// WHAT MAL IS AND IS NOT WORTH
// Only 7,078 series carry a MAL id and no AniList one, so MAL is not mainly about reaching new
// titles - it is a second, independent population voting on pairs the first one already has. That
// is worth having (agreement across two crowds is stronger evidence than either alone, and the
// vote distributions differ), but if the goal is raw coverage, finishing the AniList run is far
// cheaper per title. `--top` is the honest way to run MAL: corroborate the popular core and stop.
//
// MAL HAS NO API ANY MORE, SO THIS SCRAPES ITS PAGES
// Jikan, the unofficial MAL API, has been answering 504 "failed to connect to MyAnimeList" for
// months - the same outage that already pushed MalReviewClient off it and onto MAL's own HTML. The
// recommendations page is fetched the same way: `manga/{id}/_/userrecs`, where `_` stands in for
// the title slug MAL's routing wants but does not check. Every recommendation on that page carries
// a `/recommendations/manga/{a}-{b}` permalink holding both ids, and one "Recommended by <user>"
// line per person who submitted it, so the vote count is a count of those lines. No pagination:
// MAL renders every recommendation at once (138 of them for Berserk, spanning 400 votes).
//
// Pages are large - ~145 KB typical, 610 KB for a heavily recommended title, and even a 404 is
// 40 KB of chrome - so the client asks for gzip. Without it a full MAL pass moves about 10 GB.
//
// RATE LIMITS ARE THE WHOLE DIFFICULTY HERE
// AniList's documented ceiling is 90 req/min but it has spent long stretches degraded to 30, and the
// only honest way to find out is to read X-RateLimit-Limit off live responses. So the pacer is
// adaptive: it starts at --rpm, obeys Retry-After on a 429 exactly, and permanently backs its own
// target down when the server reports a lower ceiling than we assumed. Overrunning gets the IP
// blocked for an hour, which costs far more than pacing conservatively ever does.
//
// MAL publishes no limit at all, which is a reason to be more careful rather than less: it is a
// site being read as a site, it bans IPs that hammer it, and there is no second route to this data
// if that happens. Hence 30/min by default - one page every two seconds, slower than a person
// clicking through - and the same 429 backoff.
//
// WHAT IT DELIBERATELY DOES NOT DO, AND WHERE IT MOVED TO
// No user enumeration, no MediaListCollection, no per-user rows *here*. That was once framed as a
// road not taken; it is now a division of labour. fetch-coread-graph.cs walks Page.mediaList to
// discover users through titles, pulls each list in one MediaListCollection request, and keeps the
// per-user rows in .artifacts/coread-graph.db. The privacy line is the same one this header drew:
// raw rows never leave the machine, only the derived item-item matrix (coread-edges.db) is exported,
// and both installers refuse any file carrying user_entry/user_state/pending_user.
//
// It paid twice over. coread-edges.db is the second crowd channel (CoReadCache), and
// build-taste-vectors.cs factorizes the same interactions into taste-vectors.db, the largest
// measured gain the recommender has had. The eval starvation this block worried about is also gone:
// eval-reco-labels.cs `library` mode grades whole-library recommendations against 16,530 held-out
// reading lists, with --fold-users / --fold-out keeping the graded readers out of the training set.
//
// So nothing is missing from THIS tool. Written recommendations and finished reading lists are
// different signals - they disagree on 73% of the pairs both cover - and distribution/CLAUDE.md
// requires they stay separate artifacts and separate channels. Do not grow one into the other.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

// AniList is an API and is told plainly who is calling. MAL is not: it is a site being read as a
// site, and the request that gets an IP blocked is the one that announces itself as a crawler. Same
// browser string MalReviewClient already sends to the same host, for the same reason.
const string ApiUserAgent = "Maki-reco-graph/1.0 (+https://github.com/OrbitMPGH/Maki)";
const string BrowserUserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

var mode = "fetch";
var provider = "anilist";
var dumpPath = Path.Combine(configDir, "mangabaka.db");
var graphPath = Path.Combine(artifactsDir, "reco-graph.db");
var exportPath = Path.Combine(artifactsDir, "reco-edges.db");
var top = 0;                 // 0 = every cross-referenced title
var rpm = 0;                 // 0 = provider default
var batchSize = 10;          // AniList aliases per request; ignored by MAL
var pages = 1;               // recommendation pages per title; AniList caps a page at 25
var minVotes = 1;            // AniList ratings can go negative; a downvoted pair is not a signal
var retryErrors = false;
var maxRequests = 0;         // 0 = unlimited; a cheap way to smoke-test against the live API
var graphPathGiven = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "fetch" or "stats" or "export":
            mode = args[i];
            break;
        case "--provider":
            provider = args[++i].Trim().ToLowerInvariant();
            break;
        case "--dump":
            dumpPath = Path.GetFullPath(args[++i]);
            break;
        case "--graph":
            graphPath = Path.GetFullPath(args[++i]);
            graphPathGiven = true;
            break;
        case "--out-db":
            exportPath = Path.GetFullPath(args[++i]);
            break;
        case "--top":
            top = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--rpm":
            rpm = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--batch":
            batchSize = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--pages":
            pages = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--min-votes":
            minVotes = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--retry-errors":
            retryErrors = true;
            break;
        case "--max-requests":
            maxRequests = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            Console.WriteLine($"error: unknown argument '{args[i]}'");
            return 2;
    }
}

if (provider is not ("anilist" or "mal" or "both"))
{
    Console.WriteLine($"error: unknown provider '{provider}' (expected anilist, mal or both)");
    return 2;
}

if (!File.Exists(dumpPath))
{
    Console.WriteLine($"error: no MangaBaka dump at {dumpPath}");
    return 2;
}

batchSize = Math.Clamp(batchSize, 1, 25);
pages = Math.Clamp(pages, 1, 20);

// Aliases per request, not titles: at --pages 2 a batch of 10 titles is 20 Media selections. Left
// unbounded the query gets slow enough to age out against the client timeout, which reads as a
// batch failure and costs the whole batch.
var titlesPerRequest = Math.Max(1, batchSize / pages);

// These files used to live in the config directory. A run that resumes state is worthless if it
// silently starts from nothing, so a leftover there is an error with the fix in it rather than a
// new empty database and a lost multi-hour fetch. Delete this once no install has the old layout.
if (!graphPathGiven && !File.Exists(graphPath) && File.Exists(Path.Combine(configDir, "reco-graph.db")))
{
    Console.WriteLine($"error: no reco-graph.db in {artifactsDir}, but one exists in {configDir}.");
    Console.WriteLine("       These moved to .artifacts. Move it across (with any -wal/-shm sidecars)");
    Console.WriteLine("       or pass the old path explicitly; starting fresh would discard that run.");
    return 2;
}

Console.WriteLine($"config   : {configDir}");
Console.WriteLine($"artifacts: {artifactsDir}");
Console.WriteLine($"dump     : {dumpPath}");
Console.WriteLine($"graph    : {graphPath}");
Console.WriteLine($"mode     : {mode}");
Console.WriteLine();

using var graph = Graph.Open(graphPath);

return mode switch
{
    "stats" => Stats(),
    "export" => Export(),
    _ => await Fetch(),
};

// -------------------------------------------------------------------------------------------------
// fetch
// -------------------------------------------------------------------------------------------------
async Task<int> Fetch()
{
    string[] providers = provider == "both" ? ["anilist", "mal"] : [provider];

    var load = Stopwatch.StartNew();
    var catalogue = Catalogue.Load(dumpPath);
    Console.WriteLine($"catalogue: {catalogue.Rows.Count} cross-referenced series in {load.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"           {catalogue.ByAniList.Count} AniList ids, {catalogue.ByMal.Count} MAL ids"
        + (catalogue.Collisions > 0 ? $", {catalogue.Collisions} id collisions resolved by popularity" : string.Empty));
    Console.WriteLine();

    // Ctrl-C must land between batches, not mid-write. Cancel the token, let the loop finish its
    // current transaction, and exit reporting real progress instead of dying with the graph half
    // written and no idea which ids were done.
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("interrupt: finishing current batch, then stopping (rerun to resume)");
        cts.Cancel();
    };

    // Decompression matters here: MAL's pages are ~145 KB of HTML each and gzip takes roughly an
    // order of magnitude off a full pass. AniList's JSON benefits too, just less dramatically.
    using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

    foreach (var p in providers)
    {
        var targets = catalogue.TargetsFor(p, top);
        var done = graph.CompletedIds(p, retryErrors);
        var pending = targets.Where(t => !done.Contains(t.RemoteId)).ToList();

        Console.WriteLine($"[{p}] {targets.Count} candidates, {done.Count} already fetched, {pending.Count} to go");
        if (pending.Count == 0)
        {
            Console.WriteLine();
            continue;
        }

        var pacer = new Pacer(rpm > 0 ? rpm : p == "anilist" ? 25 : 30, p == "mal" ? 1 : 0);
        var run = p == "anilist"
            ? await FetchAniList(http, pacer, catalogue, pending, cts.Token)
            : await FetchMal(http, pacer, catalogue, pending, cts.Token);

        Console.WriteLine();
        Console.WriteLine($"[{p}] {run.Ok} with recommendations, {run.Empty} with none, {run.Missing} not found, {run.Errors} errored");
        Console.WriteLine($"[{p}] {run.Edges} edges written, {run.Unmapped} targets dropped (no MangaBaka cross-reference)");
        Console.WriteLine();

        if (cts.IsCancellationRequested)
        {
            break;
        }
    }

    graph.SetMeta("last_run_utc", DateTime.UtcNow.ToString("O"));
    Stats();
    return cts.IsCancellationRequested ? 130 : 0;
}

// AniList's GraphQL accepts aliased fields, so one request carries `batchSize` titles. That is the
// single biggest lever on wall-clock here: the rate limit counts requests, not titles.
async Task<RunTotals> FetchAniList(
    HttpClient http, Pacer pacer, Catalogue catalogue, List<Target> pending, CancellationToken ct)
{
    var totals = new RunTotals();
    var progress = new Progress(pending.Count);
    var requests = 0;

    for (var i = 0; i < pending.Count && !ct.IsCancellationRequested; i += titlesPerRequest)
    {
        if (maxRequests > 0 && requests >= maxRequests)
        {
            Console.WriteLine($"{Environment.NewLine}stopping: --max-requests {maxRequests} reached");
            break;
        }

        var batch = pending.Skip(i).Take(titlesPerRequest).ToList();
        var results = await AniListBatch(http, pacer, batch, ct);
        requests++;

        graph.WriteBatch("anilist", batch, results, catalogue, minVotes, totals);
        progress.Advance(batch.Count, totals);

        if (totals.Broken)
        {
            Console.WriteLine($"{Environment.NewLine}aborting: {totals.ErrorStreak} consecutive failures - AniList looks down, rerun with --retry-errors once it is back");
            break;
        }
    }

    return totals;
}

// Returns one result per requested id, in the same order. A null entry means the alias came back
// null - the id is gone from AniList, or points at an anime rather than a manga.
async Task<List<RemoteResult?>> AniListBatch(
    HttpClient http, Pacer pacer, List<Target> batch, CancellationToken ct)
{
    // One alias per (title, page). AniList silently clamps this connection to 25 per page and
    // reports perPage: 25 back whatever you ask for, so more recommendations means more pages, not a
    // bigger page. A heavily recommended title has hundreds; RATING_DESC means page 1 is the part
    // that carries signal and each page after it is thinner.
    var sb = new StringBuilder("query {");
    for (var i = 0; i < batch.Count; i++)
    {
        for (var p = 1; p <= pages; p++)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $" a{i}p{p}: Media(id: {batch[i].RemoteId}, type: MANGA) {{ id recommendations(sort: RATING_DESC, page: {p}, perPage: 25) {{ nodes {{ rating mediaRecommendation {{ id type }} }} }} }} ");
        }
    }

    sb.Append(" }");

    // Utf8JsonWriter rather than JsonSerializer.Serialize(new { query }): a file-based app builds
    // with reflection-free System.Text.Json, so the anonymous-object overload throws at runtime.
    var payload = new MemoryStream();
    using (var writer = new Utf8JsonWriter(payload))
    {
        writer.WriteStartObject();
        writer.WriteString("query", sb.ToString());
        writer.WriteEndObject();
    }

    var body = await Post(http, pacer, "https://graphql.anilist.co", payload.ToArray(), ct);
    var results = new List<RemoteResult?>(batch.Count);

    if (body is null)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            results.Add(RemoteResult.Failed("request failed"));
        }

        return results;
    }

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
    {
        // A whole-batch failure is nearly always one poisoned id taking the rest down with it.
        // Splitting isolates it instead of burning the other nine, and a batch of one that still
        // fails is genuinely that id's problem.
        if (batch.Count > 1)
        {
            var split = new List<RemoteResult?>();
            foreach (var one in batch)
            {
                split.AddRange(await AniListBatch(http, pacer, [one], ct));
            }

            return split;
        }

        var message = doc.RootElement.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0
            ? errs[0].TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error"
            : "no data";
        return [RemoteResult.Failed(message)];
    }

    for (var i = 0; i < batch.Count; i++)
    {
        // Page 1 is the one that decides whether the title exists at all. A null there is a dead or
        // non-manga id; a null on a later page just means the title ran out of recommendations.
        if (!data.TryGetProperty($"a{i}p1", out var first) || first.ValueKind != JsonValueKind.Object)
        {
            results.Add(null);
            continue;
        }

        var edges = new List<RemoteEdge>();
        var seen = new HashSet<int>();
        for (var p = 1; p <= pages; p++)
        {
            if (p > 1 && (!data.TryGetProperty($"a{i}p{p}", out first) || first.ValueKind != JsonValueKind.Object))
            {
                break;
            }

            if (!first.TryGetProperty("recommendations", out var recs)
                || !recs.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array
                || nodes.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var rec in nodes.EnumerateArray())
            {
                if (!rec.TryGetProperty("mediaRecommendation", out var target)
                    || target.ValueKind != JsonValueKind.Object)
                {
                    continue; // the recommended entry was deleted
                }

                // A manga page can recommend an anime. Those are real recommendations but they are
                // not edges in a manga graph, and anime ids share AniList's numbering with manga
                // ids, so keeping them would silently corrupt the mapping.
                if (!target.TryGetProperty("type", out var type) || type.GetString() != "MANGA")
                {
                    continue;
                }

                if (!target.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var targetId))
                {
                    continue;
                }

                // A page boundary can repeat an entry when votes shift between the two requests.
                if (!seen.Add(targetId))
                {
                    continue;
                }

                var rating = rec.TryGetProperty("rating", out var r) && r.TryGetInt32(out var v) ? v : 0;
                edges.Add(new RemoteEdge(targetId, rating));
            }
        }

        results.Add(RemoteResult.Ok(edges));
    }

    return results;
}

// MAL costs one page fetch per title: no batch form, and no API left to batch against. See the
// header for why this reads HTML rather than Jikan.
async Task<RunTotals> FetchMal(
    HttpClient http, Pacer pacer, Catalogue catalogue, List<Target> pending, CancellationToken ct)
{
    var totals = new RunTotals();
    var progress = new Progress(pending.Count);
    var requests = 0;

    foreach (var target in pending)
    {
        if (ct.IsCancellationRequested)
        {
            break;
        }

        if (maxRequests > 0 && requests >= maxRequests)
        {
            Console.WriteLine($"{Environment.NewLine}stopping: --max-requests {maxRequests} reached");
            break;
        }

        var page = await GetPage(http, pacer, Mal.RecommendationsUrl(target.RemoteId), ct);
        requests++;

        // NotFound is a fact about the id, not a failure to talk to MAL: the entry was deleted or
        // merged away. It has to stay distinct from an exhausted retry, or every 404 would be
        // recorded as an error and retried on every --retry-errors run for ever.
        var result = page.NotFound
            ? null
            : page.Body is null
                ? RemoteResult.Failed("request failed")
                : RemoteResult.Ok(Mal.ParseRecommendations(page.Body, target.RemoteId));

        graph.WriteBatch("mal", [target], [result], catalogue, minVotes, totals);
        progress.Advance(1, totals);

        if (totals.Broken)
        {
            Console.WriteLine($"{Environment.NewLine}aborting: {totals.ErrorStreak} consecutive failures - MAL looks unreachable or is refusing us, rerun with --retry-errors later");
            break;
        }
    }

    return totals;
}

// -------------------------------------------------------------------------------------------------
// HTTP, with the pacing and backoff that make an hours-long run survivable
// -------------------------------------------------------------------------------------------------
async Task<string?> Post(HttpClient http, Pacer pacer, string url, byte[] payload, CancellationToken ct)
    => (await Send(http, pacer, () =>
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payload),
        };
        req.Headers.Add("User-Agent", ApiUserAgent);
        req.Headers.Add("Accept", "application/json");
        req.Content.Headers.ContentType = new("application/json");
        return req;
    }, ct)).Body;

/// <summary>
/// One MAL page. Sends the browser User-Agent <c>MalReviewClient</c> already uses for the same
/// site: this is a page being read as a page, and a crawler-shaped request is the thing most likely
/// to get the IP blocked.
/// </summary>
async Task<Page> GetPage(HttpClient http, Pacer pacer, string url, CancellationToken ct)
    => await Send(http, pacer, () =>
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", BrowserUserAgent);
        req.Headers.Add("Accept", "text/html,application/xhtml+xml");
        return req;
    }, ct);

async Task<Page> Send(
    HttpClient http, Pacer pacer, Func<HttpRequestMessage> factory, CancellationToken ct)
{
    for (var attempt = 0; attempt < 5 && !ct.IsCancellationRequested; attempt++)
    {
        await pacer.WaitAsync(ct);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(factory(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Page.Failed;
        }
        catch (Exception ex)
        {
            // A transient network failure over a run this long is expected, not exceptional.
            Console.WriteLine($"{Environment.NewLine}warn: {ex.GetType().Name} - retrying in {1 << attempt}s");
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            continue;
        }

        using (response)
        {
            pacer.Observe(response);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(60);

                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.FromSeconds(60);
                }

                // A 429 means the assumed ceiling was wrong, not that this one request was unlucky.
                // Backing the target rate down permanently is what stops the run oscillating between
                // bursts and hour-long blocks.
                pacer.Throttle();
                Console.WriteLine($"{Environment.NewLine}rate limited: sleeping {wait.TotalSeconds:F0}s, target now {pacer.Rpm} req/min");
                await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Page.Missing;
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"{Environment.NewLine}warn: HTTP {(int)response.StatusCode} - retrying in {1 << attempt}s");
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
                continue;
            }

            return new Page(await response.Content.ReadAsStringAsync(ct), NotFound: false);
        }
    }

    return Page.Failed;
}

// -------------------------------------------------------------------------------------------------
// stats
// -------------------------------------------------------------------------------------------------
int Stats()
{
    var (nodes, edges) = graph.EdgeCounts();
    Console.WriteLine("graph");
    Console.WriteLine($"  nodes with outgoing edges : {nodes}");
    Console.WriteLine($"  edges                     : {edges}");

    foreach (var row in graph.PerProvider())
    {
        Console.WriteLine();
        Console.WriteLine(row.Provider);
        Console.WriteLine($"  attempted : {row.Attempted}");
        Console.WriteLine($"  with recs : {row.Ok}   none: {row.Empty}   not found: {row.Missing}   errored: {row.Errors}");
        Console.WriteLine($"  edges     : {row.Edges}");
    }

    var degrees = graph.DegreeHistogram();
    if (degrees.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("out-degree distribution (nodes with >= 1 edge)");
        foreach (var (bucket, count) in degrees)
        {
            Console.WriteLine($"  {bucket,8} : {count}");
        }
    }

    // The number that decides whether any of this is worth wiring into SemanticRecommender: how much
    // of the recommendable catalogue the graph actually reaches. A graph covering 8% of the library
    // can only ever be a third channel, never a replacement for the embeddings.
    if (File.Exists(dumpPath))
    {
        var active = Catalogue.CountActive(dumpPath);
        if (active > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"coverage : {nodes} / {active} active non-novel series ({100.0 * nodes / active:F1}%)");
        }
    }

    return 0;
}

// -------------------------------------------------------------------------------------------------
// export - the shippable artifact, one row per unordered pair
// -------------------------------------------------------------------------------------------------
int Export()
{
    // Directed rows are what the providers give and what the working table keeps, but a consumer
    // wants "how related are these two", and a pair recommended in both directions is a stronger
    // signal than one recommended in one. Folding to an unordered pair with a direction count keeps
    // that, and roughly halves the row count going out.
    if (File.Exists(exportPath))
    {
        File.Delete(exportPath);
    }

    using var outConn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = exportPath }.ToString());
    outConn.Open();
    Exec(outConn, "PRAGMA journal_mode=OFF;");
    Exec(outConn, "PRAGMA synchronous=OFF;");
    Exec(outConn, """
        CREATE TABLE pair (
            a_id           INTEGER NOT NULL,
            b_id           INTEGER NOT NULL,
            anilist_votes  INTEGER NOT NULL DEFAULT 0,
            mal_votes      INTEGER NOT NULL DEFAULT 0,
            directions     INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (a_id, b_id)
        ) WITHOUT ROWID;
        """);

    using (var attach = outConn.CreateCommand())
    {
        attach.CommandText = "ATTACH DATABASE $src AS g";
        attach.Parameters.AddWithValue("$src", graphPath);
        attach.ExecuteNonQuery();
    }

    Exec(outConn, """
        INSERT INTO pair (a_id, b_id, anilist_votes, mal_votes, directions)
        SELECT MIN(from_id, to_id), MAX(from_id, to_id),
               SUM(CASE WHEN provider = 'anilist' THEN votes ELSE 0 END),
               SUM(CASE WHEN provider = 'mal'     THEN votes ELSE 0 END),
               COUNT(DISTINCT from_id)
        FROM g.edge
        GROUP BY MIN(from_id, to_id), MAX(from_id, to_id);
        """);

    Exec(outConn, "CREATE INDEX ix_pair_b ON pair (b_id, a_id);");

    // Self-description, so a file that arrives by any route can be identified without the manifest
    // that shipped it. Maki reads generatedAt; the rest is provenance for whoever is holding an
    // artifact and wondering what is in it. Same reasoning as the meta table inside embeddings.db.
    Exec(outConn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    using (var meta = outConn.CreateCommand())
    {
        meta.CommandText = """
            INSERT INTO meta (key, value) VALUES
                ('schemaVersion', '1'),
                ('generatedAt', $at),
                ('pairCount', (SELECT COUNT(*) FROM pair)),
                ('seriesCount', (SELECT COUNT(*) FROM (SELECT a_id AS id FROM pair UNION SELECT b_id FROM pair))),
                ('providers', $providers)
            """;
        meta.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));

        // Read off the edges rather than hardcoded: an artifact from an AniList-only run must not
        // claim MAL data, because RecoGraphCache decides whether to scale a provider's votes by
        // whether that provider is present at all.
        meta.Parameters.AddWithValue("$providers", string.Join(",", Providers(outConn)));
        meta.ExecuteNonQuery();
    }

    Exec(outConn, "DETACH DATABASE g;");
    Exec(outConn, "VACUUM;");

    using var count = outConn.CreateCommand();
    count.CommandText = "SELECT COUNT(*), SUM(CASE WHEN directions = 2 THEN 1 ELSE 0 END) FROM pair";
    using var reader = count.ExecuteReader();
    reader.Read();

    Console.WriteLine($"exported : {exportPath}");
    Console.WriteLine($"  pairs      : {reader.GetInt64(0)}");
    Console.WriteLine($"  reciprocal : {(reader.IsDBNull(1) ? 0 : reader.GetInt64(1))}");
    Console.WriteLine($"  size       : {new FileInfo(exportPath).Length / 1024.0 / 1024.0:F1} MB");
    return 0;
}

/// <summary>Providers that actually contributed an edge to the exported pairs, in a stable order.</summary>
static List<string> Providers(SqliteConnection outConn)
{
    var found = new List<string>();
    foreach (var (name, column) in new[] { ("anilist", "anilist_votes"), ("mal", "mal_votes") })
    {
        using var cmd = outConn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM pair WHERE {column} > 0)";
        if (cmd.ExecuteScalar() is long present && present == 1)
        {
            found.Add(name);
        }
    }

    return found;
}

static void Exec(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

// -------------------------------------------------------------------------------------------------
// types
// -------------------------------------------------------------------------------------------------

/// <summary>
/// A fetched page. <see cref="NotFound"/> separates "the remote has no such manga", which is
/// settled and should never be retried, from a request that simply did not get through.
/// </summary>
internal readonly record struct Page(string? Body, bool NotFound)
{
    public static Page Missing => new(null, NotFound: true);

    public static Page Failed => new(null, NotFound: false);
}

/// <summary>One title to fetch: its MangaBaka id and the provider id standing in for it.</summary>
internal readonly record struct Target(int MangaBakaId, int RemoteId, int Popularity);

internal readonly record struct RemoteEdge(int RemoteTargetId, int Votes);

internal sealed class RemoteResult
{
    public List<RemoteEdge> Edges { get; private init; } = [];

    public string? Error { get; private init; }

    public static RemoteResult Ok(List<RemoteEdge> edges) => new() { Edges = edges };

    public static RemoteResult Failed(string error) => new() { Error = error };
}

internal sealed class RunTotals
{
    /// <summary>
    /// Consecutive failures ends the run for that provider. Without it a provider-wide outage - or an
    /// IP ban, which is the realistic MAL failure - walks the whole candidate list marking every id
    /// 'error', and the next run skips all of them because they count as attempted. An outage would
    /// silently become a permanently empty half of the graph.
    /// </summary>
    public const int ErrorStreakLimit = 12;

    public int Ok;
    public int Empty;
    public int Missing;
    public int Errors;
    public int Edges;
    public int Unmapped;
    public int ErrorStreak;

    public bool Broken => ErrorStreak >= ErrorStreakLimit;
}

/// <summary>
/// Cross-reference tables lifted out of the MangaBaka dump once, up front. Every provider id the
/// fetch sees has to come back through here or the edge is meaningless, so it is a plain in-memory
/// dictionary rather than a query per lookup.
/// </summary>
internal sealed class Catalogue
{
    // popularity_global_current is a rank: 1 is the most popular title in the dump. An unranked row
    // has to sort last rather than first, which a raw NULL would not.
    private const int Unranked = int.MaxValue;

    public required List<Row> Rows { get; init; }

    public required Dictionary<int, int> ByAniList { get; init; }

    public required Dictionary<int, int> ByMal { get; init; }

    public int Collisions { get; private init; }

    public static Catalogue Load(string dumpPath)
    {
        var rows = new List<Row>(300_000);
        var popularityById = new Dictionary<int, int>(300_000);
        var byAniList = new Dictionary<int, int>(150_000);
        var byMal = new Dictionary<int, int>(90_000);
        var collisions = 0;

        using var conn = OpenReadOnly(dumpPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source_anilist_id, source_my_anime_list_id, popularity_global_current
            FROM series
            WHERE state = 'active' AND type != 'novel'
              AND (source_anilist_id IS NOT NULL OR source_my_anime_list_id IS NOT NULL)
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = (int)reader.GetInt64(0);
            var aniList = reader.IsDBNull(1) ? (int?)null : (int)reader.GetInt64(1);
            var mal = reader.IsDBNull(2) ? (int?)null : (int)reader.GetInt64(2);
            var popularity = reader.IsDBNull(3) ? Unranked : (int)reader.GetInt64(3);

            rows.Add(new Row(id, aniList, mal, popularity));
            popularityById[id] = popularity;

            // Two MangaBaka rows can claim the same remote id (a split entry, a bad cross-reference).
            // Keep the more popular one: it is the row a user is actually looking at, and taking
            // whichever came last would make the mapping depend on dump row order.
            Claim(byAniList, aniList, id, popularity, popularityById, ref collisions);
            Claim(byMal, mal, id, popularity, popularityById, ref collisions);
        }

        return new Catalogue
        {
            Rows = rows,
            ByAniList = byAniList,
            ByMal = byMal,
            Collisions = collisions,
        };

        static void Claim(
            Dictionary<int, int> map,
            int? remoteId,
            int id,
            int popularity,
            Dictionary<int, int> popularityById,
            ref int collisions)
        {
            if (remoteId is not { } key)
            {
                return;
            }

            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = id;
                return;
            }

            collisions++;
            if (popularity < popularityById[existing])
            {
                map[key] = id;
            }
        }
    }

    public static int CountActive(string dumpPath)
    {
        using var conn = OpenReadOnly(dumpPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM series WHERE state = 'active' AND type != 'novel'";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Titles to fetch for a provider, most popular first. Popularity order is not cosmetic: an
    /// interrupted run, or a deliberate <c>--top</c>, then holds the part of the graph most queries
    /// touch rather than an arbitrary slice of the tail.
    /// </summary>
    public List<Target> TargetsFor(string provider, int top)
    {
        var targets = Rows
            .Select(r => (r.Id, Remote: provider == "anilist" ? r.AniListId : r.MalId, r.Popularity))
            .Where(t => t.Remote is not null)
            .OrderBy(t => t.Popularity)
            .Select(t => new Target(t.Id, t.Remote!.Value, t.Popularity))
            .ToList();

        return top > 0 ? targets.Take(top).ToList() : targets;
    }

    public bool TryMap(string provider, int remoteId, out int mangaBakaId)
        => (provider == "anilist" ? ByAniList : ByMal).TryGetValue(remoteId, out mangaBakaId);

    internal static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        return conn;
    }

    internal readonly record struct Row(int Id, int? AniListId, int? MalId, int Popularity);
}

/// <summary>The working database: edges, and enough per-id state to resume an interrupted run.</summary>
internal sealed class Graph : IDisposable
{
    private readonly SqliteConnection _conn;

    private Graph(SqliteConnection conn) => _conn = conn;

    public static Graph Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();

        // WAL because the run is thousands of small transactions over hours and a power cut mid-run
        // should cost the last batch, not the file. synchronous=NORMAL is the matching trade: this is
        // a rebuildable derived artifact, not the library.
        Run(conn, "PRAGMA journal_mode=WAL;");
        Run(conn, "PRAGMA synchronous=NORMAL;");
        Run(conn, """
            CREATE TABLE IF NOT EXISTS edge (
                from_id  INTEGER NOT NULL,
                to_id    INTEGER NOT NULL,
                provider TEXT    NOT NULL,
                votes    INTEGER NOT NULL,
                PRIMARY KEY (from_id, to_id, provider)
            ) WITHOUT ROWID;
            """);
        Run(conn, "CREATE INDEX IF NOT EXISTS ix_edge_to ON edge (to_id, from_id);");
        Run(conn, """
            CREATE TABLE IF NOT EXISTS fetch_state (
                provider     TEXT    NOT NULL,
                remote_id    INTEGER NOT NULL,
                mangabaka_id INTEGER NOT NULL,
                status       TEXT    NOT NULL,
                edges        INTEGER NOT NULL DEFAULT 0,
                unmapped     INTEGER NOT NULL DEFAULT 0,
                fetched_at   TEXT    NOT NULL,
                note         TEXT,
                PRIMARY KEY (provider, remote_id)
            ) WITHOUT ROWID;
            """);
        Run(conn, "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");

        return new Graph(conn);
    }

    /// <summary>
    /// Remote ids already attempted. Errors count as attempted unless the caller asks to retry them:
    /// a permanently broken id re-attempted on every run would never let the fetch finish.
    /// </summary>
    public HashSet<int> CompletedIds(string provider, bool retryErrors)
    {
        var set = new HashSet<int>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = retryErrors
            ? "SELECT remote_id FROM fetch_state WHERE provider = $p AND status != 'error'"
            : "SELECT remote_id FROM fetch_state WHERE provider = $p";
        cmd.Parameters.AddWithValue("$p", provider);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add((int)reader.GetInt64(0));
        }

        return set;
    }

    public void WriteBatch(
        string provider,
        IReadOnlyList<Target> batch,
        IReadOnlyList<RemoteResult?> results,
        Catalogue catalogue,
        int minVotes,
        RunTotals totals)
    {
        using var tx = _conn.BeginTransaction();

        using var edge = _conn.CreateCommand();
        edge.Transaction = tx;
        edge.CommandText = """
            INSERT INTO edge (from_id, to_id, provider, votes) VALUES ($f, $t, $p, $v)
            ON CONFLICT (from_id, to_id, provider) DO UPDATE SET votes = excluded.votes
            """;
        var fp = edge.Parameters.Add("$f", SqliteType.Integer);
        var tp = edge.Parameters.Add("$t", SqliteType.Integer);
        edge.Parameters.AddWithValue("$p", provider);
        var vp = edge.Parameters.Add("$v", SqliteType.Integer);

        using var state = _conn.CreateCommand();
        state.Transaction = tx;
        state.CommandText = """
            INSERT INTO fetch_state (provider, remote_id, mangabaka_id, status, edges, unmapped, fetched_at, note)
            VALUES ($p, $r, $m, $s, $e, $u, $at, $n)
            ON CONFLICT (provider, remote_id) DO UPDATE SET
                status = excluded.status, edges = excluded.edges, unmapped = excluded.unmapped,
                fetched_at = excluded.fetched_at, note = excluded.note
            """;
        state.Parameters.AddWithValue("$p", provider);
        var rp = state.Parameters.Add("$r", SqliteType.Integer);
        var mp = state.Parameters.Add("$m", SqliteType.Integer);
        var sp = state.Parameters.Add("$s", SqliteType.Text);
        var ep = state.Parameters.Add("$e", SqliteType.Integer);
        var up = state.Parameters.Add("$u", SqliteType.Integer);
        state.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        var np = state.Parameters.Add("$n", SqliteType.Text);

        for (var i = 0; i < batch.Count; i++)
        {
            var target = batch[i];
            var result = i < results.Count ? results[i] : null;

            string status;
            var written = 0;
            var unmapped = 0;
            string? note = null;

            if (result is null)
            {
                status = "missing";
                totals.Missing++;
                totals.ErrorStreak = 0;
            }
            else if (result.Error is not null)
            {
                status = "error";
                note = result.Error;
                totals.Errors++;
                totals.ErrorStreak++;
            }
            else
            {
                totals.ErrorStreak = 0;
                foreach (var e in result.Edges)
                {
                    if (e.Votes < minVotes)
                    {
                        continue;
                    }

                    // The remote recommends a title MangaBaka has no cross-reference for. Nothing to
                    // do but count it - an edge to an id the rest of Maki cannot resolve is dead
                    // weight in the index and a foot-gun for anything that joins on it.
                    if (!catalogue.TryMap(provider, e.RemoteTargetId, out var to))
                    {
                        unmapped++;
                        continue;
                    }

                    if (to == target.MangaBakaId)
                    {
                        continue; // two remote entries folded into one MangaBaka row
                    }

                    fp.Value = target.MangaBakaId;
                    tp.Value = to;
                    vp.Value = e.Votes;
                    edge.ExecuteNonQuery();
                    written++;
                }

                status = written > 0 ? "ok" : "empty";
                if (written > 0)
                {
                    totals.Ok++;
                }
                else
                {
                    totals.Empty++;
                }

                totals.Edges += written;
                totals.Unmapped += unmapped;
            }

            rp.Value = target.RemoteId;
            mp.Value = target.MangaBakaId;
            sp.Value = status;
            ep.Value = written;
            up.Value = unmapped;
            np.Value = (object?)note ?? DBNull.Value;
            state.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public (long Nodes, long Edges) EdgeCounts()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT from_id), COUNT(*) FROM edge";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public List<ProviderStats> PerProvider()
    {
        var rows = new List<ProviderStats>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT provider,
                   COUNT(*),
                   SUM(status = 'ok'), SUM(status = 'empty'),
                   SUM(status = 'missing'), SUM(status = 'error'),
                   SUM(edges)
            FROM fetch_state GROUP BY provider ORDER BY provider
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ProviderStats(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? 0 : reader.GetInt64(6)));
        }

        return rows;
    }

    public List<(string Bucket, long Count)> DegreeHistogram()
    {
        var rows = new List<(string, long)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            WITH d AS (SELECT from_id, COUNT(*) AS n FROM edge GROUP BY from_id)
            SELECT CASE WHEN n = 1 THEN '1'
                        WHEN n <= 4 THEN '2-4'
                        WHEN n <= 9 THEN '5-9'
                        WHEN n <= 24 THEN '10-24'
                        ELSE '25+' END AS bucket,
                   COUNT(*)
            FROM d GROUP BY bucket ORDER BY MIN(n)
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ($k, $v)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    private static void Run(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal readonly record struct ProviderStats(
        string Provider, long Attempted, long Ok, long Empty, long Missing, long Errors, long Edges);
}

/// <summary>
/// Request pacing. Holds a target rate and never exceeds it, revises that target down when the
/// server reports a lower ceiling than assumed, and never revises it back up - an hour-long IP block
/// costs more than every second conservative pacing wastes.
/// </summary>
internal sealed class Pacer(int rpm, int maxPerSecond)
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
            if (maxPerSecond > 0)
            {
                var floor = TimeSpan.FromSeconds(1.0 / maxPerSecond);
                if (spacing < floor)
                {
                    spacing = floor;
                }
            }

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

    /// <summary>
    /// Reads the ceiling off a live response. AniList's documented 90/min has spent long stretches
    /// degraded to 30, and this header is the only place that says which one is in force right now.
    /// </summary>
    public void Observe(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Limit", out var values)
            || !int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            || limit <= 0)
        {
            return;
        }

        // Stay under the stated ceiling rather than at it: the server's window is not aligned to
        // ours, so pacing exactly at the limit still produces 429s at the boundary.
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

/// <summary>One rewriting status line, with an ETA computed from measured throughput.</summary>
internal sealed class Progress(int total)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _done;
    private DateTime _lastPrint = DateTime.MinValue;

    public void Advance(int n, RunTotals totals)
    {
        _done += n;

        // Rewriting faster than a few times a second is invisible and, redirected to a file, turns a
        // progress line into megabytes.
        if (_done < total && DateTime.UtcNow - _lastPrint < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        _lastPrint = DateTime.UtcNow;
        var rate = _done / Math.Max(1.0, _clock.Elapsed.TotalSeconds);
        var eta = rate > 0 ? TimeSpan.FromSeconds((total - _done) / rate) : TimeSpan.Zero;

        Console.Write($"\r  {_done}/{total}  {rate * 60:F0}/min  {totals.Edges} edges  ETA {Format(eta)}      ");
    }

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m" : $"{(int)t.TotalMinutes}m{t.Seconds:D2}s";
}

/// <summary>
/// Reads MyAnimeList's public recommendations page. MAL has no working API left (see the header),
/// so this parses the HTML the site serves, the same route <c>MalReviewClient</c> already takes for
/// reviews.
/// </summary>
file static class Mal
{
    /// <summary>
    /// MAL's routing wants <c>manga/{id}/{title-slug}/{section}</c> but never checks the slug, so
    /// <c>_</c> stands in for a title this tool does not know and does not need.
    /// </summary>
    public static string RecommendationsUrl(int malId) =>
        $"https://myanimelist.net/manga/{malId}/_/userrecs";

    /// <summary>
    /// Every recommendation block opens with a permalink carrying both ids of the pair, which is a
    /// far steadier anchor than the layout around it: the title link appears twice per block (cover
    /// and heading) and its markup is presentational, while this is the site's own identifier for
    /// the recommendation.
    /// </summary>
    private static readonly Regex Permalink = new(
        @"/recommendations/manga/(\d+)-(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// One per person who submitted this pair. MAL prints them all inline with no pagination, so
    /// counting them between one permalink and the next is the vote count - there is no number on
    /// the page to read instead.
    /// </summary>
    private static readonly Regex RecommendedBy = new(@"Recommended by", RegexOptions.Compiled);

    public static List<RemoteEdge> ParseRecommendations(string html, int sourceMalId)
    {
        var edges = new List<RemoteEdge>();
        var anchors = Permalink.Matches(html);

        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            if (!int.TryParse(anchor.Groups[1].Value, out var a)
                || !int.TryParse(anchor.Groups[2].Value, out var b))
            {
                continue;
            }

            // The permalink names the pair in id order, so the recommended title is whichever half
            // is not the page we asked for. A pair naming neither is somebody else's recommendation
            // leaking in from the page furniture.
            var other = a == sourceMalId ? b : b == sourceMalId ? a : 0;
            if (other == 0)
            {
                continue;
            }

            var blockEnd = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
            var votes = RecommendedBy.Matches(html[anchor.Index..blockEnd]).Count;

            // A block that parsed but showed no attribution still represents one recommendation;
            // treating it as zero would silently drop it at the caller's MinVotes floor.
            edges.Add(new RemoteEdge(other, Math.Max(1, votes)));
        }

        return edges;
    }
}
