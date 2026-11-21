using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Maki.Api.Tests;

/// <summary>Runs only when <c>PROFILE_PROBE_DIR</c> is set, so CI never does.</summary>
public sealed class ProfileProbeFactAttribute : FactAttribute
{
    public ProfileProbeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROFILE_PROBE_DIR")))
        {
            Skip = "Research probe. Set PROFILE_PROBE_DIR; see distribution/reco-profile-probe.py.";
        }
    }
}

/// <summary>
/// The real Recommended service on held-out reading lists, one throwaway library per reader, written
/// by <c>distribution/reco-profile-probe.py select</c>. Refuses to boot when any reader-trained
/// artifact saw the held-out fold or a probed reader built the page-share table.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class RecommendationServiceProfileProbe
{
    private static readonly string[] ReaderTrained =
        ["coread-edges.db", "taste-vectors.db", "ease-table.db", "reader-cohorts.db"];

    private static readonly string[] Untrained = ["embeddings.db", "reco-edges.db"];

    private const string PageTable = "page-share-fold.tsv";

    private sealed class Reader(int id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => id;
        public string UserName => "reader";
        public MakiPermission Permissions => MakiPermission.Admin;
        public bool AllRootFolders => true;
        public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
        public string MaxContentRating => "pornographic";
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("The profile probe is offline.");
    }

    private sealed record Entry(int MangaBakaId, string Status, int Score, int Chapters, int Read);

    private sealed record Profile(string Key, long UserId, string Group);

    [ProfileProbeFact]
    public async Task Pages()
    {
        var root = Environment.GetEnvironmentVariable("PROFILE_PROBE_DIR")!;
        var dumpDir = Environment.GetEnvironmentVariable("PROFILE_PROBE_DUMP_DIR")
            ?? throw new InvalidOperationException("Set PROFILE_PROBE_DUMP_DIR to the folder holding mangabaka.db.");
        using var selection = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "selection.json")));
        var fold = selection.RootElement.GetProperty("fold").GetString()!;
        var parts = fold.Split('/');
        var (foldIndex, foldCount) = (int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture));

        var run = Path.Combine(root, "run");
        Directory.CreateDirectory(run);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "cfg")))
        {
            var target = Path.Combine(run, Path.GetFileName(file));
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(file).Length)
            {
                File.Copy(file, target, overwrite: true);
            }
        }

        GuardArtifacts(run, fold, foldIndex, foldCount);
        var profiles = File.ReadAllLines(Path.Combine(root, "users.tsv")).Skip(1)
            .Select(line => line.Split('\t'))
            .Select(c => new Profile(c[0], long.Parse(c[1], CultureInfo.InvariantCulture), c[2]))
            .ToList();
        var builders = File.ReadAllLines(Path.Combine(root, "page-table-readers.txt"))
            .Where(l => l.Length > 0).Select(l => long.Parse(l, CultureInfo.InvariantCulture)).ToHashSet();
        foreach (var p in profiles)
        {
            if (FoldOf(p.UserId, foldCount) != foldIndex || builders.Contains(p.UserId))
            {
                throw new InvalidOperationException($"{p.Key} is not a held-out reader the artifacts never saw.");
            }
        }

        if (Environment.GetEnvironmentVariable("PROFILE_PROBE_ONLY") is { Length: > 0 } only)
        {
            var keep = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            profiles = profiles.Where(p => keep.Contains(p.Key) || keep.Contains(p.Group)).ToList();
        }

        var cases = File.ReadAllLines(Path.Combine(root, "cases.tsv"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('\t'))
            .Select(c => (Name: c[0], Options: Options(c.Length > 1 ? c[1] : string.Empty)))
            .ToList();
        if (Environment.GetEnvironmentVariable("PROFILE_PROBE_CASES") is { Length: > 0 } pick)
        {
            var keep = pick.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            cases = cases.Where(c => keep.Contains(c.Name)).ToList();
        }

        CookieSession.EnsureWebRoot();
        foreach (var (name, options) in cases)
        {
            await RunCaseAsync(root, run, dumpDir, name, options, profiles, fold, foldIndex, foldCount);
        }
    }

    private static Dictionary<string, string> Options(string raw) => raw
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(kv => kv.Split('=', 2))
        .ToDictionary(kv => kv[0].Trim(), kv => kv.Length > 1 ? kv[1].Trim() : "true");

    private static double Num(Dictionary<string, string> options, string key, double fallback) =>
        options.TryGetValue(key, out var raw) ? double.Parse(raw, CultureInfo.InvariantCulture) : fallback;

    /// <summary>The harness's reader fold (<c>UserFold.Of</c> in eval-reco-labels.cs): FNV-1a over the id's eight bytes.</summary>
    private static int FoldOf(long userId, int folds)
    {
        var hash = 2166136261u;
        var value = (ulong)userId;
        for (var i = 0; i < 8; i++)
        {
            hash = (hash ^ (byte)(value >> (i * 8))) * 16777619u;
        }

        return (int)(hash % (uint)folds);
    }

    private static void GuardArtifacts(string dir, string fold, int foldIndex, int foldCount)
    {
        var want = Enumerable.Range(0, foldCount).Where(f => f != foldIndex).ToHashSet();
        foreach (var name in ReaderTrained)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"{name} is missing; the probe runs only on fold-limited artifacts.");
            }

            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'trainingFold'";
            var trained = (cmd.ExecuteScalar() as string ?? "all")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : -1)
                .ToHashSet();
            if (!trained.SetEquals(want))
            {
                throw new InvalidOperationException($"{name} was not trained on exactly the folds outside {fold}.");
            }
        }

        foreach (var file in Directory.GetFiles(dir, "*.db"))
        {
            var name = Path.GetFileName(file);
            if (!ReaderTrained.Contains(name) && !Untrained.Contains(name) && name != "maki.db")
            {
                throw new InvalidOperationException($"{name} is not an artifact the probe knows to be held out.");
            }
        }

        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "page-share-fold.json")));
        if (provenance.RootElement.GetProperty("foldUsers").GetString() != fold
            || !File.Exists(Path.Combine(dir, PageTable)))
        {
            throw new InvalidOperationException("The page-share table was not built on the held-out fold.");
        }
    }

    private static async Task DeleteDatabaseAsync(string dir)
    {
        foreach (var f in new[] { "maki.db", "maki.db-wal", "maki.db-shm" })
        {
            await RetryAsync(() => File.Delete(Path.Combine(dir, f)));
        }
    }

    private static async Task RetryAsync(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 40)
            {
                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(500);
            }
        }
    }

    private static async Task RunCaseAsync(string root, string run, string dumpDir, string name,
        Dictionary<string, string> options, List<Profile> profiles, string fold, int foldIndex, int foldCount)
    {
        await DeleteDatabaseAsync(run);
        var table = options.TryGetValue("easetable", out var tableName)
            ? Path.Combine(root, "cfg", "ease", $"{tableName}.db")
            : Path.Combine(root, "cfg", "ease-table.db");
        await RetryAsync(() =>
        {
            File.Delete(Path.Combine(run, "ease-table.weights"));
            File.Copy(table, Path.Combine(run, "ease-table.db"), overwrite: true);
        });
        GuardArtifacts(run, fold, foldIndex, foldCount);

        var outDir = Path.Combine(root, "pages", name);
        Directory.CreateDirectory(outDir);
        var saved = new[] { "MAKI_CONFIG_DIR", "MAKI_SHARED_DUMP_DIR", "MAKI_DUMP_BOOT_DOWNLOAD", "MAKI_ARTIFACT_IDLE_MINUTES" }
            .ToDictionary(k => k, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", run);
        Environment.SetEnvironmentVariable("MAKI_SHARED_DUMP_DIR", dumpDir);
        Environment.SetEnvironmentVariable("MAKI_DUMP_BOOT_DOWNLOAD", "0");
        Environment.SetEnvironmentVariable("MAKI_ARTIFACT_IDLE_MINUTES", "0");
        try
        {
            using var baseFactory = new WebApplicationFactory<Program>();
            using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(o =>
                    o.HttpMessageHandlerBuilderActions.Add(h => h.PrimaryHandler = new OfflineHandler()));
                var reco = Maki.Metadata.Embedding.RecommenderTuning.Default;
                services.AddSingleton(reco with
                {
                    MinReasonShare = Num(options, "share", reco.MinReasonShare),
                    CommonWeight = Num(options, "commonweight", reco.CommonWeight),
                    FameCapRatio = Num(options, "famecap", reco.FameCapRatio),
                });
                var ease = Maki.Metadata.Ease.EaseTuning.Default;
                services.AddSingleton(ease with { Weight = Num(options, "easeweight", ease.Weight) });
                services.AddSingleton(Maki.Core.Recommendations.TasteTuning.Default with
                {
                    UnreadSeedScale = Num(options, "unreadscale", 1),
                    OffShelfSeedScale = Num(options, "offshelf", 1),
                });
                services.AddSingleton(Maki.Metadata.Taste.TasteVectorTuning.Default with
                {
                    MissingShare = Num(options, "tastemissing", 0),
                });
                services.AddSingleton(new Maki.Metadata.PageShare.PageShareOptions(Path.Combine(run, PageTable)));
            }));

            var settings = factory.Services.GetRequiredService<IAppSettings>();
            await settings.SetAsync(SettingKeys.RecommendationsEase, options.GetValueOrDefault("ease", "true"));
            await settings.SetAsync(SettingKeys.RecommendationsPageShare, options.GetValueOrDefault("pageshare", "true"));

            int userId, rootId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                var user = new MakiUser
                {
                    UserName = "reader", NormalizedUserName = "READER", Permissions = MakiPermission.Admin,
                    AllRootFolders = true, MaxContentRating = "pornographic",
                };
                db.Users.Add(user);
                var folder = new RootFolder { Path = Path.Combine(run, "library") };
                db.RootFolders.Add(folder);
                await db.SaveChangesAsync();
                (userId, rootId) = (user.Id, folder.Id);
            }

            var service = factory.Services.GetRequiredService<RecommendationService>();
            var summary = new StringBuilder("key\tgroup\tlibrary\tseeds\tavoided\tsimilar\trelated\tms\n");
            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                var entries = File.ReadAllLines(Path.Combine(root, "users", $"{profile.Key}.library.tsv")).Skip(1)
                    .Where(l => l.Length > 0)
                    .Select(l => l.Split('\t'))
                    .Select(c => new Entry(int.Parse(c[0], CultureInfo.InvariantCulture), c[1],
                        int.Parse(c[2], CultureInfo.InvariantCulture), int.Parse(c[3], CultureInfo.InvariantCulture),
                        int.Parse(c[4], CultureInfo.InvariantCulture)))
                    .ToList();
                await ReplaceLibraryAsync(factory.Services, userId, rootId, (i + 1) * 1000, entries);

                var reader = new Reader(userId);
                var clock = Stopwatch.StartNew();
                var similar = new List<MangaBakaRecommendation>();
                var related = 0;
                for (var page = 0; similar.Count < 40; page++)
                {
                    var result = await service.GetAsync(new RecommendationRequest(
                        Filters: RecommendationFilters.None, Page: page, Obscurity: Num(options, "obscurity", 0)), reader);
                    related = result.Related.Count;
                    similar.AddRange(result.Similar);
                    if (!result.HasMore)
                    {
                        break;
                    }
                }

                clock.Stop();
                int seeds, avoided;
                using (var scope = factory.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                    db.Scope.SetUser(userId, true);
                    var snapshot = await factory.Services.GetRequiredService<SeedWeightService>().SnapshotAsync(db, reader);
                    (seeds, avoided) = (snapshot.Effective.EligibleIds.Count, snapshot.Avoided.Count);
                }

                var tsv = new StringBuilder("rank\tid\tkind\tnamed_id\n");
                for (var r = 0; r < similar.Count; r++)
                {
                    var p = similar[r];
                    var (kind, named) = p.RelationKind is not null ? ("relation", p.RelatedToId)
                        : p.BecauseOfTitle is not null ? ("feels", p.BecauseOfId)
                        : p.AuthorMatch ? ("author", null)
                        : p.CoRead && p.CoReadWithTitle is not null ? ("fans", p.CoReadWithId)
                        : p.CoRead ? ("readers", null)
                        : p.CoRecommended ? ("recommended", null)
                        : p.TasteMatch ? ("taste", null)
                        : ("similar", (string?)null);
                    tsv.Append(r + 1).Append('\t').Append(p.ProviderId).Append('\t').Append(kind).Append('\t')
                        .Append(named).Append('\n');
                }

                await File.WriteAllTextAsync(Path.Combine(outDir, $"{profile.Key}.tsv"), tsv.ToString());
                summary.Append(CultureInfo.InvariantCulture,
                    $"{profile.Key}\t{profile.Group}\t{entries.Count}\t{seeds}\t{avoided}\t{similar.Count}\t{related}\t{clock.ElapsedMilliseconds}\n");
            }

            await File.WriteAllTextAsync(Path.Combine(outDir, "summary.tsv"), summary.ToString());
        }
        finally
        {
            foreach (var (key, value) in saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static ReadingStatus StatusOf(string anilist) => anilist switch
    {
        "COMPLETED" => ReadingStatus.Completed,
        "CURRENT" or "REPEATING" => ReadingStatus.Reading,
        "PAUSED" => ReadingStatus.OnHold,
        "DROPPED" => ReadingStatus.Dropped,
        _ => ReadingStatus.PlanToRead,
    };

    /// <summary>
    /// Swaps the library for one reader's list. Series ids never repeat between readers, because the
    /// seed snapshot cache keys on the library's count and highest id.
    /// </summary>
    private static async Task ReplaceLibraryAsync(
        IServiceProvider services, int userId, int rootId, int baseId, List<Entry> entries)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.Scope.SetUser(userId, true);
        await db.ChapterProgress.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Chapters.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.UserSeriesStates.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Series.IgnoreQueryFilters().ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        var lastRead = now.AddDays(-60);
        var rows = new List<(Series Series, Entry Entry)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var series = new Series
            {
                Id = baseId + i, Title = $"s{e.MangaBakaId}", SortTitle = $"s{e.MangaBakaId}",
                MangaBakaId = e.MangaBakaId, RootFolderId = rootId, FolderName = $"s{e.MangaBakaId}", Added = now,
            };
            for (var c = 1; c <= e.Chapters; c++)
            {
                series.Chapters.Add(new Chapter { Number = c, Language = "en", FileRemovedAt = lastRead });
            }

            db.Series.Add(series);
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = userId, SeriesId = series.Id, Rating = e.Score > 0 ? Math.Max(1, e.Score / 10.0) : null,
                Status = StatusOf(e.Status), UpdatedAt = now,
            });
            rows.Add((series, e));
        }

        await db.SaveChangesAsync();
        foreach (var (series, e) in rows)
        {
            foreach (var chapter in series.Chapters.OrderBy(c => c.Number).Take(e.Read))
            {
                db.ChapterProgress.Add(new ChapterProgress
                {
                    UserId = userId, SeriesId = series.Id, ChapterId = chapter.Id, PageCount = 20, PageIndex = 19,
                    Completed = true, StartedAt = lastRead, UpdatedAt = lastRead, CompletedAt = lastRead,
                });
            }
        }

        await db.SaveChangesAsync();
        foreach (var group in rows.GroupBy(r => StatusOf(r.Entry.Status)))
        {
            var ids = group.Select(r => r.Series.Id).ToList();
            var status = group.Key;
            await db.UserSeriesStates.IgnoreQueryFilters().Where(s => ids.Contains(s.SeriesId))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, status));
        }
    }
}
