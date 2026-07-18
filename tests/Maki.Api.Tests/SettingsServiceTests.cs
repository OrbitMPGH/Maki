using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// The AppConfig-backed <see cref="SettingsService"/>: round-tripping values, upserting in
/// place, and the null/whitespace-deletes contract that keeps the table free of empty rows.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;

    public SettingsServiceTests() => _settings = new SettingsService(_db.ScopeFactory());

    public void Dispose() => _db.Dispose();

    private int RowCount()
    {
        using var db = _db.NewContext();
        return db.AppConfig.Count();
    }

    [Fact]
    public async Task Missing_key_reads_as_null()
    {
        Assert.Null(await _settings.GetAsync("nope"));
    }

    [Fact]
    public async Task Set_then_get_round_trips()
    {
        await _settings.SetAsync("qbittorrent.password", "hunter2");
        Assert.Equal("hunter2", await _settings.GetAsync("qbittorrent.password"));
    }

    [Fact]
    public async Task Set_on_an_existing_key_updates_in_place()
    {
        await _settings.SetAsync("prowlarr.apikey", "old");
        await _settings.SetAsync("prowlarr.apikey", "new");

        Assert.Equal("new", await _settings.GetAsync("prowlarr.apikey"));
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task Setting_null_removes_the_entry()
    {
        await _settings.SetAsync("kavita.apikey", "token");
        await _settings.SetAsync("kavita.apikey", null);

        Assert.Null(await _settings.GetAsync("kavita.apikey"));
        Assert.Equal(0, RowCount());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Setting_blank_removes_the_entry(string blank)
    {
        await _settings.SetAsync("mal.secret", "token");
        await _settings.SetAsync("mal.secret", blank);

        Assert.Null(await _settings.GetAsync("mal.secret"));
        Assert.Equal(0, RowCount());
    }

    [Fact]
    public async Task Removing_a_missing_key_is_a_no_op()
    {
        await _settings.SetAsync("ghost", null);
        Assert.Equal(0, RowCount());
    }

    [Fact]
    public async Task Distinct_keys_coexist()
    {
        await _settings.SetAsync("a", "1");
        await _settings.SetAsync("b", "2");

        Assert.Equal("1", await _settings.GetAsync("a"));
        Assert.Equal("2", await _settings.GetAsync("b"));
        Assert.Equal(2, RowCount());
    }
}
