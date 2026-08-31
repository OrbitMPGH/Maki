using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The reader's own taste profile. Most of these are about the two things that make the page honest
/// rather than merely populated: which series each view counts, and when a ratio is too thin to be
/// worth showing.
/// </summary>
public class TasteProfileServiceTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly TestDb _db = new();
    private readonly Dictionary<long, MangaBakaProfileRow> _rows = [];

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A store with no dump on disk, answering only the profile query, from what the test seeded.
    /// </summary>
    private sealed class FakeStore(IReadOnlyDictionary<long, MangaBakaProfileRow> rows) : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyDictionary<long, MangaBakaProfileRow>> GetProfileRowsAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, MangaBakaProfileRow>>(
                ids.Where(rows.ContainsKey).ToDictionary(id => id, id => rows[id]));
    }

    /// <summary>
    /// A cache pointed at paths that do not exist, so <c>GetAsync</c> hands back null the way it
    /// does on an install whose index has never been built.
    /// </summary>
    private static VectorIndexCache NoIndex() => new(
        new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base),
        new MangaBakaDumpOptions("", ""),
        NullLogger<VectorIndexCache>.Instance);

    private TasteProfileService Service(TasteTuning? tuning = null)
    {
        var effective = tuning ?? TasteTuning.Default;
        var settings = new FakeAppSettings();
        var behavioural = new BehavioralTasteService(effective);
        return new TasteProfileService(
            _db.ScopeFactory(),
            new SeedWeightService(behavioural, effective, settings),
            behavioural,
            new FakeStore(_rows),
            NoIndex(),
            NullLogger<TasteProfileService>.Instance);
    }

    /// <summary>Seeds a library series and the dump row the profile aggregates it from.</summary>
    private int SeedSeries(
        int mangaBakaId,
        string[]? genres = null,
        (string Name, string Bucket, bool Spoiler)[]? tags = null,
        string[]? authors = null,
        string type = "manga",
        int year = 2015,
        IncognitoMode incognito = IncognitoMode.Off)
    {
        _rows[mangaBakaId] = new MangaBakaProfileRow(
            mangaBakaId,
            $"Series {mangaBakaId}",
            genres ?? [],
            [.. (tags ?? []).Select(t => new MangaBakaTag(t.Name, t.Bucket, null, t.Spoiler))],
            authors ?? [],
            [],
            type,
            year);

        return _db.SeedSeries($"Series {mangaBakaId}", configure: s =>
        {
            s.MangaBakaId = mangaBakaId;
            s.Incognito = incognito;
        });
    }

    /// <summary>Reads the whole series through, so it lands in the read population.</summary>
    private void SeedFinished(int seriesId, int chapters = 40, int userId = 1)
    {
        using var db = _db.NewContext();
        for (var i = 1; i <= chapters; i++)
        {
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}-{i}.cbz", DateAdded = Now };
            db.ChapterFiles.Add(file);
            db.SaveChanges();

            var chapter = new Chapter { SeriesId = seriesId, Number = i, ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();

            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = userId,
                SeriesId = seriesId,
                ChapterId = chapter.Id,
                PageCount = 20,
                Completed = true,
                ReadSeconds = 600,
                StartedAt = Now,
                UpdatedAt = Now
            });
            db.SaveChanges();
        }
    }

    private Task<TasteProfile> ProfileAsync(TasteView view, bool refresh = false, int userId = 1) =>
        Service().GetAsync(new TestCurrentUser(userId), view, refresh);

    private static double ShareOf(IReadOnlyList<TasteFacet> facets, string name) =>
        facets.Single(f => f.Name == name).Share;

    [Fact]
    public async Task Read_view_counts_only_what_was_read()
    {
        SeedFinished(SeedSeries(101, genres: ["Action"]));
        SeedSeries(202, genres: ["Romance"]);

        var profile = await ProfileAsync(TasteView.Read);

        Assert.Equal(1, profile.SeriesCount);
        Assert.Equal(2, profile.LibraryCount);
        Assert.Equal("Action", Assert.Single(profile.Genres).Name);
    }

    [Fact]
    public async Task Shelf_view_counts_an_unread_series_at_neutral()
    {
        SeedFinished(SeedSeries(101, genres: ["Action"]));
        SeedSeries(202, genres: ["Romance"]);

        var profile = await ProfileAsync(TasteView.Shelf);

        Assert.Equal(2, profile.SeriesCount);
        // The read series carries a weight above neutral, so it takes the larger slice; the unread
        // one is present rather than absent, which is the whole point of the shelf view.
        Assert.True(ShareOf(profile.Genres, "Action") > ShareOf(profile.Genres, "Romance"));
        Assert.True(ShareOf(profile.Genres, "Romance") > 0);
    }

    [Fact]
    public async Task Fully_incognito_reading_is_in_neither_view()
    {
        SeedFinished(SeedSeries(101, genres: ["Action"], incognito: IncognitoMode.Full));

        // The ChapterProgress rows exist for incognito reading, so this gate has to hold here too.
        Assert.Empty((await ProfileAsync(TasteView.Read)).Genres);

        // The shelf view still owns the series, it just cannot claim it was read.
        var shelf = await ProfileAsync(TasteView.Shelf);
        Assert.Equal(1, shelf.SeriesCount);
    }

    [Fact]
    public async Task Spoiler_tags_never_reach_the_profile()
    {
        SeedFinished(SeedSeries(101, tags: [("Amnesia", "core", true), ("Time Travel", "core", false)]));

        var tags = (await ProfileAsync(TasteView.Read)).Tags;

        Assert.Equal("Time Travel", Assert.Single(tags).Name);
    }

    [Fact]
    public async Task Tag_buckets_weight_against_each_other()
    {
        SeedFinished(SeedSeries(101, tags: [("Core Thing", "core", false), ("Passing Thing", "incidental", false)]));

        var tags = (await ProfileAsync(TasteView.Read)).Tags;

        Assert.Equal("Core Thing", tags[0].Name);
        Assert.True(tags[0].Weight > tags[1].Weight);
    }

    [Fact]
    public async Task Credit_sentinels_are_not_creators()
    {
        SeedFinished(SeedSeries(101, authors: ["Various", "Inio Asano"]));

        Assert.Equal("Inio Asano", Assert.Single((await ProfileAsync(TasteView.Read)).Creators).Name);
    }

    [Fact]
    public async Task Over_index_compares_the_read_share_against_a_flat_library()
    {
        // Read: three Action series. Owned: those plus three unread Romance. Action is 100% of the
        // read share against half the shelf, so it over-indexes 2x, on enough series to say so.
        SeedFinished(SeedSeries(101, genres: ["Action"]));
        SeedFinished(SeedSeries(102, genres: ["Action"]));
        SeedFinished(SeedSeries(103, genres: ["Action"]));
        for (var i = 0; i < 3; i++)
        {
            SeedSeries(200 + i, genres: ["Romance"]);
        }

        var action = (await ProfileAsync(TasteView.Read)).Genres.Single(g => g.Name == "Action");

        Assert.Equal(3, action.Support);
        Assert.Equal(1.0, action.Share, 6);
        Assert.NotNull(action.OverIndexShelf);
        Assert.Equal(2.0, action.OverIndexShelf!.Value, 6);
    }

    [Fact]
    public async Task A_genre_matching_the_library_over_indexes_at_one()
    {
        SeedFinished(SeedSeries(101, genres: ["Action"]));
        SeedFinished(SeedSeries(102, genres: ["Action"]));
        SeedFinished(SeedSeries(103, genres: ["Action"]));

        var action = (await ProfileAsync(TasteView.Read)).Genres.Single(g => g.Name == "Action");

        Assert.Equal(1.0, action.OverIndexShelf!.Value, 6);
    }

    [Fact]
    public async Task Thin_support_carries_no_ratio()
    {
        // Two series is below the floor: the ratio would be arithmetic on one or two rows.
        SeedFinished(SeedSeries(101, genres: ["Action"]));
        SeedFinished(SeedSeries(102, genres: ["Action"]));

        var action = (await ProfileAsync(TasteView.Read)).Genres.Single(g => g.Name == "Action");

        Assert.Equal(2, action.Support);
        Assert.Null(action.OverIndexShelf);
        Assert.True(action.Share > 0); // the facet itself is real, only the ratio is withheld
    }

    [Fact]
    public async Task Another_users_reading_never_leaks_in()
    {
        var other = _db.SeedUser("other");
        SeedFinished(SeedSeries(101, genres: ["Action"]), userId: other);

        Assert.Empty((await ProfileAsync(TasteView.Read)).Genres);
        Assert.Equal(1, (await ProfileAsync(TasteView.Read, userId: other)).SeriesCount);
    }

    [Fact]
    public async Task Missing_vector_index_costs_only_the_catalogue_column()
    {
        SeedFinished(SeedSeries(101, genres: ["Action"]));
        SeedFinished(SeedSeries(102, genres: ["Action"]));
        SeedFinished(SeedSeries(103, genres: ["Action"]));

        var profile = await ProfileAsync(TasteView.Read);

        Assert.False(profile.CatalogueBaselineAvailable);
        Assert.All(profile.Genres, g => Assert.Null(g.OverIndexCatalogue));
        Assert.NotNull(profile.Genres.Single(g => g.Name == "Action").OverIndexShelf);
    }

    [Fact]
    public async Task An_empty_library_answers_rather_than_throwing()
    {
        var profile = await ProfileAsync(TasteView.Read);

        Assert.Equal(0, profile.SeriesCount);
        Assert.Empty(profile.Genres);
        Assert.Empty(profile.Years);
    }

    [Fact]
    public async Task Each_view_caches_separately_and_refresh_rebuilds()
    {
        var service = Service();
        var user = new TestCurrentUser(1);
        SeedFinished(SeedSeries(101, genres: ["Action"]));

        var read = await service.GetAsync(user, TasteView.Read, refresh: false);
        var shelf = await service.GetAsync(user, TasteView.Shelf, refresh: false);
        Assert.NotSame(read, shelf); // one key per view, so the two never serve each other

        Assert.Same(read, await service.GetAsync(user, TasteView.Read, refresh: false));
        Assert.NotSame(read, await service.GetAsync(user, TasteView.Read, refresh: true));
    }

    [Fact]
    public async Task Years_come_back_in_order()
    {
        SeedFinished(SeedSeries(101, year: 2021));
        SeedFinished(SeedSeries(102, year: 1998));

        var years = (await ProfileAsync(TasteView.Read)).Years;

        Assert.Equal([1998, 2021], years.Select(y => y.Year));
        Assert.Equal(1.0, years.Sum(y => y.Share), 6);
    }
}
