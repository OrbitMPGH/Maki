using System.Collections.Concurrent;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="State">"waiting", "measuring", "done", "failed" (nothing measured), or "skipped" (no chapter to sample).</param>
/// <param name="Problem">Why the last failed sample failed: "cooldown" (the source is rate-limiting us) or "failed".</param>
public sealed record ScoutSourceProgress(int MappingId, string State, int Planned, int Done, int Measured, string? Problem);

/// <param name="Probes">Chapter samples planned across every source.</param>
/// <param name="Done">Samples attempted so far, measured or not.</param>
/// <param name="Measured">Samples that produced a measurement.</param>
/// <param name="Chapters">The chapters being sampled, as their source labels, in reading order.</param>
public sealed record ScoutSnapshot(
    bool Running, int Probes, int Done, int Measured, DateTime StartedAtUtc, DateTime? FinishedAtUtc,
    IReadOnlyList<string> Chapters, IReadOnlyList<ScoutSourceProgress> Sources);

/// <summary>
/// Measures every linked source of one series on the same few chapters, so its source order is
/// measured before anything is downloaded. Chapters come from <see cref="ScoutChapters"/>; each is
/// sampled with the upgrade scan's probe and recorded as a <see cref="SourceQualityOrigin.Probe"/>
/// sample. Runs detached and queues behind a small gate, so matching a whole import list does not
/// probe every source at once; asking again for a series already being measured joins that run.
/// </summary>
public sealed class SourceScoutService(
    IServiceScopeFactory scopes,
    SourceRegistry registry,
    SourceAvailability availability,
    SourceProbeService probes,
    IDownloadCooldown cooldowns,
    ILogger<SourceScoutService> logger)
{
    public const int ChaptersPerSource = 3;
    private const int MaxConcurrentSeries = 2;
    private const int MaxParallelSources = 3;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<int, Job> _jobs = new();
    private readonly SemaphoreSlim _gate = new(MaxConcurrentSeries);

    private sealed class SourceState(int mappingId, int planned)
    {
        public int MappingId { get; } = mappingId;
        public int Planned { get; } = planned;
        public int Done;
        public int Measured;
        public volatile bool Started;
        public volatile string? Problem;

        public ScoutSourceProgress Snapshot()
        {
            var state = Planned == 0 ? "skipped"
                : Done >= Planned ? (Measured > 0 ? "done" : "failed")
                : Started ? "measuring"
                : "waiting";
            return new ScoutSourceProgress(MappingId, state, Planned, Done, Measured, Problem);
        }
    }

    private sealed class Job
    {
        public required DateTime StartedAtUtc { get; init; }
        public volatile IReadOnlyList<SourceState> Sources = [];
        public volatile IReadOnlyList<string> Chapters = [];
        public volatile bool Running = true;
        public DateTime? FinishedAtUtc;

        public ScoutSnapshot Snapshot()
        {
            var sources = Sources;
            return new ScoutSnapshot(Running, sources.Sum(s => s.Planned), sources.Sum(s => s.Done),
                sources.Sum(s => s.Measured), StartedAtUtc, FinishedAtUtc, Chapters,
                [.. sources.Select(s => s.Snapshot())]);
        }
    }

    public ScoutSnapshot? Snapshot(int seriesId) => _jobs.TryGetValue(seriesId, out var job) ? job.Snapshot() : null;

    public ScoutSnapshot Start(int seriesId)
    {
        var fresh = new Job { StartedAtUtc = DateTime.UtcNow };
        var job = _jobs.AddOrUpdate(seriesId, fresh, (_, existing) => existing.Running ? existing : fresh);
        if (ReferenceEquals(job, fresh))
        {
            _ = Task.Run(() => RunAsync(seriesId, fresh));
        }

        return job.Snapshot();
    }

    /// <summary>The automatic run after matching, only when <see cref="SettingKeys.SourcesScoutOnMatch"/> is on.</summary>
    public async Task StartIfEnabledAsync(MakiDbContext db, int seriesId, CancellationToken ct)
    {
        var enabled = await db.AppConfig.AsNoTracking()
            .AnyAsync(c => c.Key == SettingKeys.SourcesScoutOnMatch && c.Value == "true", ct);
        if (enabled)
        {
            Start(seriesId);
        }
    }

    /// <summary>For tests: the run for <paramref name="seriesId"/>, awaited to completion.</summary>
    internal async Task RunNowAsync(int seriesId, CancellationToken ct)
    {
        var job = new Job { StartedAtUtc = DateTime.UtcNow };
        _jobs[seriesId] = job;
        try
        {
            await ScoutAsync(seriesId, job, ct);
        }
        finally
        {
            job.FinishedAtUtc = DateTime.UtcNow;
            job.Running = false;
        }
    }

    private async Task RunAsync(int seriesId, Job job)
    {
        using var cts = new CancellationTokenSource(Deadline);
        var ct = cts.Token;
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                await ScoutAsync(seriesId, job, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Measuring the sources of series {SeriesId} stopped early", seriesId);
        }
        finally
        {
            job.FinishedAtUtc = DateTime.UtcNow;
            job.Running = false;
        }
    }

    private async Task ScoutAsync(int seriesId, Job job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var disabled = await availability.DisabledAsync(ct);
        var mappings = (await db.SourceMappings.AsNoTracking()
                .Where(m => m.SeriesId == seriesId && m.Enabled)
                .OrderBy(m => m.Priority)
                .ThenBy(m => m.Id)
                .ToListAsync(ct))
            .Where(m => !disabled.Contains(m.SourceName) && registry.Find(m.SourceName) is not null)
            .ToList();
        var mappingIds = mappings.Select(m => m.Id).ToList();
        var links = await db.ChapterSourceLinks.AsNoTracking()
            .Where(l => mappingIds.Contains(l.SourceMappingId))
            .Include(l => l.Chapter)
            .ToListAsync(ct);

        var picks = ScoutChapters.Pick(
            [.. links.GroupBy(l => l.ChapterId).Select(g => new ScoutChapters.Candidate(
                g.Key, g.First().Chapter!.Number, g.Select(l => l.SourceMappingId).Distinct().Count()))],
            ChaptersPerSource, Random.Shared).ToHashSet();
        var plan = mappings
            .Select(m => (Mapping: m, Links: links
                .Where(l => l.SourceMappingId == m.Id && picks.Contains(l.ChapterId))
                .DistinctBy(l => l.ChapterId)
                .ToList()))
            .ToList();
        var states = plan.ToDictionary(p => p.Mapping.Id, p => new SourceState(p.Mapping.Id, p.Links.Count));
        job.Chapters = [.. links
            .Where(l => picks.Contains(l.ChapterId))
            .DistinctBy(l => l.ChapterId)
            .OrderBy(l => l.Chapter!.Number ?? decimal.MaxValue)
            .Select(l => l.Chapter!.NumberRaw ?? l.Chapter.Number?.ToString() ?? "?")];
        job.Sources = [.. states.Values];

        using var writes = new SemaphoreSlim(1);
        await Parallel.ForEachAsync(plan.Where(p => p.Links.Count > 0),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSources, CancellationToken = ct },
            async (entry, token) =>
            {
                var state = states[entry.Mapping.Id];
                state.Started = true;
                var source = registry.Find(entry.Mapping.SourceName)!;
                foreach (var link in entry.Links)
                {
                    var chapter = link.Chapter!;
                    ProbeResult? probe = null;
                    try
                    {
                        probe = await probes.ProbeAsync(source, entry.Mapping.SourceName, new SourceChapter(
                                entry.Mapping.SourceName, entry.Mapping.SourceSeriesId, link.SourceChapterId,
                                chapter.NumberRaw, chapter.Number, chapter.Volume, chapter.Title, chapter.Language,
                                chapter.ReleaseDate),
                            UpgradeScanService.SampleCount, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Sampling {Source} chapter {Chapter} failed",
                            entry.Mapping.SourceName, chapter.NumberRaw);
                    }

                    if (probe is null)
                    {
                        state.Problem = cooldowns.Remaining(entry.Mapping.SourceName) > TimeSpan.Zero ? "cooldown" : "failed";
                    }
                    else
                    {
                        long? size = probe.SampledPages > 0 ? probe.SampleBytes / probe.SampledPages * probe.PageCount : null;
                        await writes.WaitAsync(token);
                        try
                        {
                            await SourceQualitySamples.RecordAsync(db, entry.Mapping, chapter.Id, SourceQualityOrigin.Probe,
                                probe.PageCount, probe.MedianWidth, probe.MedianHeight, size, probe.ImageFormat,
                                DateTime.UtcNow, token);
                            await db.SaveChangesAsync(token);
                        }
                        finally
                        {
                            writes.Release();
                        }

                        Interlocked.Increment(ref state.Measured);
                    }

                    Interlocked.Increment(ref state.Done);
                }
            });

        var snapshot = job.Snapshot();
        logger.LogInformation("Measured {Measured} of {Probes} chapter samples across {Sources} sources of series {SeriesId}",
            snapshot.Measured, snapshot.Probes, plan.Count, seriesId);
    }
}
