using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// <see cref="IUserSettings"/> over the request's own DbContext and the request's own user. One round
/// trip per call, on the connection the request is already using.
/// </summary>
public class UserSettingsService(MakiDbContext db, ICurrentUser currentUser) : IUserSettings
{
    public int UserId => currentUser.UserId;

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        UserSettingsStore.GetAsync(db, UserId, key, ct);

    public Task<Dictionary<string, string>> GetManyAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
        UserSettingsStore.GetManyAsync(db, UserId, keys, ct);

    public Task SetAsync(string key, string? value, CancellationToken ct = default) =>
        UserSettingsStore.SetAsync(db, UserId, key, value, ct);
}

/// <summary>
/// <see cref="IUserSettingsStore"/> for code with no request behind it — a singleton that opens a
/// short-lived scope per call, the same shape as <see cref="SettingsService"/>. Callers that need
/// several keys at once should take a DbContext and use <see cref="UserSettingsStore"/> directly
/// rather than paying a scope per key here.
/// </summary>
public class UserSettingsStoreService(IServiceScopeFactory scopeFactory) : IUserSettingsStore
{
    public async Task<string?> GetAsync(int userId, string key, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await UserSettingsStore.GetAsync(db, userId, key, ct);
    }

    public async Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await UserSettingsStore.SetAsync(db, userId, key, value, ct);
    }
}

/// <summary>
/// The queries behind <see cref="IUserSettings"/>, as plain functions over a context and an explicit
/// user id. Background jobs need them for a user who is not "the current user" — the scrobble tick
/// reads every connected user's tracker toggles — and they run with an unrestricted
/// <see cref="DataScope"/>, where the query filter would not narrow anything on its own. Passing the
/// id explicitly is what keeps those loops honest.
/// </summary>
public static class UserSettingsStore
{
    public static async Task<string?> GetAsync(
        MakiDbContext db, int userId, string key, CancellationToken ct = default)
    {
        var row = await db.UserSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Key == key, ct);
        return row?.Value;
    }

    public static async Task<Dictionary<string, string>> GetManyAsync(
        MakiDbContext db, int userId, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var rows = await db.UserSettings
            .AsNoTracking()
            .Where(s => s.UserId == userId && keys.Contains(s.Key))
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Value);
    }

    public static async Task SetAsync(
        MakiDbContext db, int userId, string key, string? value, CancellationToken ct = default)
    {
        var row = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId && s.Key == key, ct);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (row is not null)
            {
                db.UserSettings.Remove(row);
            }
        }
        else if (row is null)
        {
            db.UserSettings.Add(new UserSetting { UserId = userId, Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }
}
