using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;

namespace Maki.Api.Tests;

/// <summary>
/// The aggregate behind behavioural seed weighting. Every query it runs bypasses the global query
/// filter, so most of these tests are about the gates that therefore have to be written by hand:
/// whose reading it is, which root folders the caller can see, and incognito.
/// </summary>
public class BehavioralTasteServiceTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly TestDb _db = new();
    private readonly BehavioralTasteService _service = new(TasteTuning.Default);

    public void Dispose() => _db.Dispose();

    private int SeedSeries(int mangaBakaId, IncognitoMode incognito = IncognitoMode.Off) =>
        _db.SeedSeries($"Series {mangaBakaId}", configure: s =>
        {
            s.MangaBakaId = mangaBakaId;
            s.Incognito = incognito;
        });

    /// <summary>Seeds a chapter, downloaded unless told otherwise, and returns its id.</summary>
    private int SeedChapter(int seriesId, decimal number, bool downloaded = true)
    {
        using var db = _db.NewContext();
        int? fileId = null;
        if (downloaded)
        {
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = $"{seriesId}-{number}.cbz",
                DateAdded = Now
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            fileId = file.Id;
        }

        var chapter = new Chapter { SeriesId = seriesId, Number = number, ChapterFileId = fileId };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void SeedProgress(
        int userId, int seriesId, int chapterId, bool completed = true,
        int readSeconds = 600, DateTime? updatedAt = null, DateTime? unreadAt = null)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId,
            SeriesId = seriesId,
            ChapterId = chapterId,
            PageIndex = 0,
            PageCount = 20,
            Completed = completed,
            ReadSeconds = readSeconds,
            UnreadAt = unreadAt,
            StartedAt = updatedAt ?? Now,
            UpdatedAt = updatedAt ?? Now
        });
        db.SaveChanges();
    }

    /// <summary>Reads the whole series through, so the weight lands well clear of neutral.</summary>
    private void SeedFinished(int userId, int seriesId, int chapters = 40)
    {
        for (var i = 1; i <= chapters; i++)
        {
            SeedProgress(userId, seriesId, SeedChapter(seriesId, i));
        }
    }

    private async Task<IReadOnlyDictionary<long, double>> WeightsAsync(
        int userId, IReadOnlyCollection<long> visible, TasteTuning? tuning = null)
    {
        using var db = _db.NewContext();
        var service = tuning is null ? _service : new BehavioralTasteService(tuning);
        return await service.WeightsAsync(db, userId, visible);
    }

    private async Task<IReadOnlyDictionary<long, SeriesReadSignal>> SignalsAsync(
        int userId, IReadOnlyCollection<long> visible)
    {
        using var db = _db.NewContext();
        return await _service.ReadSignalsAsync(db, userId, visible);
    }

    /// <summary>
    /// The split behind the taste profile: a series read only a little implies a neutral weight and
    /// so carries no entry in <see cref="BehavioralTasteService.WeightsAsync"/>, but it was still
    /// read and the profile has to be able to see that.
    /// </summary>
    [Fact]
    public async Task Read_signals_keep_what_the_weights_drop()
    {
        var seriesId = SeedSeries(101);
        // One chapter of forty, no time banked: real reading, but nothing the weight function will
        // move off neutral.
        for (var i = 1; i <= 40; i++)
        {
            var chapterId = SeedChapter(seriesId, i);
            if (i == 1)
            {
                SeedProgress(1, seriesId, chapterId, readSeconds: 0);
            }
        }

        var signals = await SignalsAsync(1, [101L]);

        Assert.Equal(1, signals[101].Completed);
        Assert.Equal(40, signals[101].Downloaded);
    }

    [Fact]
    public async Task Read_signals_apply_the_same_gates_as_the_weights()
    {
        var other = _db.SeedUser("other");
        SeedFinished(1, SeedSeries(101));
        SeedFinished(1, SeedSeries(202, IncognitoMode.Full));
        SeedFinished(other, SeedSeries(303));

        var signals = await SignalsAsync(1, [101L, 202L, 303L]);

        // Incognito and another user's reading are excluded here, not downstream, so both answers
        // stay in step.
        Assert.Equal([101L], signals.Keys.Order());
    }

    [Fact]
    public async Task Read_signals_ignore_series_the_caller_cannot_see()
    {
        SeedFinished(1, SeedSeries(101));

        Assert.Empty(await SignalsAsync(1, [999L]));
    }

    [Fact]
    public async Task Weights_a_series_the_user_read_through()
    {
        var seriesId = SeedSeries(101);
        SeedFinished(userId: 1, seriesId);

        var weights = await WeightsAsync(1, [101L]);

        Assert.True(weights[101] > TasteWeights.Neutral);
    }

    [Fact]
    public async Task Fully_incognito_series_is_excluded()
    {
        var seriesId = SeedSeries(101, IncognitoMode.Full);
        SeedFinished(userId: 1, seriesId);

        // The ChapterProgress rows exist for incognito reading (only the StatsEvents are suppressed),
        // so this gate has to be written out here or the series leaks into the seed weighting.
        Assert.Empty(await WeightsAsync(1, [101L]));
    }

    [Fact]
    public async Task Scrobble_only_incognito_still_counts()
    {
        var seriesId = SeedSeries(101, IncognitoMode.ScrobbleOnly);
        SeedFinished(userId: 1, seriesId);

        Assert.True((await WeightsAsync(1, [101L])).ContainsKey(101));
    }

    [Fact]
    public async Task Another_users_reading_never_leaks_in()
    {
        var other = _db.SeedUser("other");
        var seriesId = SeedSeries(101);
        SeedFinished(other, seriesId);

        Assert.Empty(await WeightsAsync(1, [101L]));
        Assert.True((await WeightsAsync(other, [101L]))[101] > TasteWeights.Neutral);
    }

    [Fact]
    public async Task Series_outside_the_callers_visible_library_is_dropped()
    {
        var seriesId = SeedSeries(101);
        SeedFinished(userId: 1, seriesId);

        // The reading queries run with filters off, so root-folder visibility comes back only from
        // intersecting with the library the caller read under their own scope.
        Assert.Empty(await WeightsAsync(1, [202L]));
    }

    [Fact]
    public async Task Series_with_no_reading_produces_no_entry()
    {
        SeedSeries(101);
        var read = SeedSeries(202);
        SeedFinished(userId: 1, read);

        var weights = await WeightsAsync(1, [101L, 202L]);

        Assert.False(weights.ContainsKey(101));
        Assert.True(weights.ContainsKey(202));
    }

    [Fact]
    public async Task Series_with_no_mangabaka_id_produces_no_entry()
    {
        var seriesId = _db.SeedSeries("Unmatched");
        SeedFinished(userId: 1, seriesId);

        Assert.Empty(await WeightsAsync(1, [101L]));
    }

    [Fact]
    public async Task Tombstoned_chapters_do_not_count_as_read()
    {
        var seriesId = SeedSeries(101);
        for (var i = 1; i <= 40; i++)
        {
            SeedProgress(1, seriesId, SeedChapter(seriesId, i), completed: false, unreadAt: Now);
        }

        Assert.Empty(await WeightsAsync(1, [101L]));
    }

    [Fact]
    public async Task Chapters_that_are_not_downloaded_do_not_count_as_read()
    {
        var seriesId = SeedSeries(101);
        for (var i = 1; i <= 40; i++)
        {
            SeedProgress(1, seriesId, SeedChapter(seriesId, i, downloaded: false));
        }

        Assert.Empty(await WeightsAsync(1, [101L]));
    }

    [Fact]
    public async Task A_barely_touched_series_weighs_below_a_finished_one()
    {
        var finished = SeedSeries(101);
        SeedFinished(userId: 1, finished);

        var dipped = SeedSeries(202);
        for (var i = 1; i <= 40; i++)
        {
            SeedChapter(dipped, i);
        }

        SeedProgress(1, dipped, SeedChapter(dipped, 41), readSeconds: 120);

        var weights = await WeightsAsync(1, [101L, 202L]);

        Assert.True(weights[202] < TasteWeights.Neutral);
        Assert.True(weights[101] > weights[202]);
    }

    [Fact]
    public async Task Uniform_tuning_returns_nothing()
    {
        var seriesId = SeedSeries(101);
        SeedFinished(userId: 1, seriesId);

        Assert.Empty(await WeightsAsync(1, [101L], TasteTuning.Uniform));
    }

    [Fact]
    public async Task Unauthenticated_scope_returns_nothing()
    {
        var seriesId = SeedSeries(101);
        SeedFinished(userId: 1, seriesId);

        Assert.Empty(await WeightsAsync(0, [101L]));
    }

    [Fact]
    public async Task Two_local_series_sharing_a_catalogue_entry_keep_the_strongest_evidence()
    {
        // MangaBakaId carries no unique index, so this is legal state (a re-add, a split release).
        var finished = SeedSeries(101);
        SeedFinished(userId: 1, finished);

        var dipped = SeedSeries(101);
        for (var i = 1; i <= 40; i++)
        {
            SeedChapter(dipped, i);
        }

        SeedProgress(1, dipped, SeedChapter(dipped, 41), readSeconds: 60);

        var weights = await WeightsAsync(1, [101L]);

        Assert.Single(weights);
        Assert.True(weights[101] > TasteWeights.Neutral);
    }
}
