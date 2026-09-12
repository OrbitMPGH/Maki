using System.Globalization;
using System.Runtime;
using Maki.Metadata.Catalogue;
using Maki.Metadata.CoRead;
using Maki.Metadata.Embedding;
using Maki.Metadata.ReaderCohorts;
using Maki.Metadata.RecoGraph;
using Maki.Sources.Common;

namespace Maki.Api.Services;

/// <summary>
/// Where the process's memory actually is, broken down far enough to tell apart the three things a
/// single "Maki is using 900 MB" number cannot.
///
/// <para>
/// First, managed heap against everything else. The recommendation artifacts live on the managed
/// heap and are the part we can shrink in code; the ONNX embedder's weights, SQLite and the runtime
/// itself are native and never appear in <c>GC.GetTotalMemory</c>.
/// </para>
///
/// <para>
/// Second, live bytes against garbage the collector has not got to yet. Workstation GC on a mostly
/// idle process sits on a lot of collectable heap, so a reading taken without a collection says
/// very little. <c>collect</c> forces one, and the pair of readings is the answer.
/// </para>
///
/// <para>
/// Third, and the one that most often makes a container look twice its size: anonymous memory
/// against page cache. Container memory as most dashboards report it is the cgroup's
/// <c>memory.current</c>, which counts every file page the kernel cached on the app's behalf, so
/// every archive a scan read and every cover written lands in the number an operator is watching.
/// Those pages are reclaimable and are not the app holding anything, but they are indistinguishable
/// from a leak if one number is all you have. <c>RssAnon</c> against <c>file</c> separates them.
/// </para>
/// </summary>
public sealed class MemoryDiagnostics(
    CatalogueIndexCache catalogue,
    CoReadCache coRead,
    RecoGraphCache recoGraph,
    ReaderCohortCache cohorts,
    VectorIndexCache vectors,
    TextEmbedder embedder,
    IEnumerable<IIdleBrowser> browsers)
{
    public object Snapshot(bool collect)
    {
        long? beforeCollect = null;
        if (collect)
        {
            beforeCollect = GC.GetTotalMemory(false);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        var info = GC.GetGCMemoryInfo();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();

        return new
        {
            managed = new
            {
                totalBytes = GC.GetTotalMemory(false),
                beforeCollectBytes = beforeCollect,
                heapSizeBytes = info.HeapSizeBytes,
                committedBytes = info.TotalCommittedBytes,
                fragmentedBytes = info.FragmentedBytes,
                generations = Generations(info),
                serverGc = GCSettings.IsServerGC,
                latencyMode = GCSettings.LatencyMode.ToString(),
                gen2Collections = GC.CollectionCount(2)
            },
            process = new
            {
                workingSetBytes = process.WorkingSet64,
                privateBytes = process.PrivateMemorySize64,
                // The managed heap is a subset of the working set. What is left is the runtime, the
                // ONNX session's native arena, SQLite, and file pages mapped into the process.
                nativeApproxBytes = Math.Max(0, process.WorkingSet64 - info.TotalCommittedBytes)
            },
            linux = ReadLinux(),
            artifacts = new object[]
            {
                Artifact("catalogue-indexes", catalogue.IsLoaded, catalogue.IdleFor),
                Artifact("co-read-graph", coRead.IsLoaded, coRead.IdleFor),
                Artifact("reco-graph", recoGraph.IsLoaded, recoGraph.IdleFor),
                Artifact("reader-cohorts", cohorts.IsLoaded, cohorts.IdleFor),
                Artifact("search-vectors", vectors.IsLoaded, idle: null),
                Artifact("text-embedder", embedder.IsReady, idle: null)
            },
            // Native, and the largest single thing on the list when one is up: the Playwright
            // driver and the headless shell together. Nothing here can see their size from inside
            // the process, so this reports only whether they are running.
            browsers = browsers
                .Select(b => new { name = b.BrowserName, running = b.IsRunning })
                .ToArray()
        };
    }

    private static object Artifact(string name, bool loaded, TimeSpan? idle) =>
        new { name, loaded, idleSeconds = loaded && idle is { } i ? (long?)i.TotalSeconds : null };

    private static object[] Generations(GCMemoryInfo info)
    {
        // gen0, gen1, gen2, LOH, POH, in that order, and the runtime may report fewer.
        string[] names = ["gen0", "gen1", "gen2", "loh", "poh"];
        var generations = info.GenerationInfo;
        var result = new object[Math.Min(names.Length, generations.Length)];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new { name = names[i], sizeBytes = generations[i].SizeAfterBytes };
        }

        return result;
    }

    /// <summary>
    /// The kernel's own accounting, which is the only place the page-cache question can be
    /// answered. Null off Linux, and null per field where this kernel does not publish it: cgroup
    /// v1 and v2 spell all of this differently and a NAS may be running either.
    /// </summary>
    private static object? ReadLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var status = ReadKeyedFile("/proc/self/status", ':');
        var v2Stat = ReadKeyedFile("/sys/fs/cgroup/memory.stat", ' ');
        var stat = v2Stat.Count > 0 ? v2Stat : ReadKeyedFile("/sys/fs/cgroup/memory/memory.stat", ' ');

        return new
        {
            // /proc/self/status reports kB; everything else here is bytes already.
            rssAnonBytes = Scale(status.GetValueOrDefault("RssAnon"), 1024),
            rssFileBytes = Scale(status.GetValueOrDefault("RssFile"), 1024),
            rssShmemBytes = Scale(status.GetValueOrDefault("RssShmem"), 1024),
            vmRssBytes = Scale(status.GetValueOrDefault("VmRSS"), 1024),
            cgroupCurrentBytes = ReadLong("/sys/fs/cgroup/memory.current")
                ?? ReadLong("/sys/fs/cgroup/memory/memory.usage_in_bytes"),
            cgroupLimitBytes = ReadLong("/sys/fs/cgroup/memory.max")
                ?? ReadLong("/sys/fs/cgroup/memory/memory.limit_in_bytes"),
            cgroupAnonBytes = Parse(stat.GetValueOrDefault("anon") ?? stat.GetValueOrDefault("rss")),
            cgroupFileBytes = Parse(stat.GetValueOrDefault("file") ?? stat.GetValueOrDefault("cache")),
            cgroupSlabBytes = Parse(stat.GetValueOrDefault("slab"))
        };
    }

    private static Dictionary<string, string> ReadKeyedFile(string path, char separator)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var at = line.IndexOf(separator);
                if (at > 0)
                {
                    values[line[..at].Trim()] = line[(at + 1)..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A kernel that does not publish this file. The caller renders those fields null.
        }

        return values;
    }

    private static long? ReadLong(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // "12345 kB" from /proc, "max" from an unlimited cgroup.
        var token = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? Scale(string? value, long factor) => Parse(value) * factor;
}
