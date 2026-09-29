using System.Collections.Concurrent;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Singleton IAppSettings over the AppConfig table (short-lived scopes per read), cached in
/// process. Every runtime write goes through <see cref="SetAsync"/>; the startup marker rows and
/// migrations that write AppConfig directly finish before <see cref="Invalidate"/> runs at boot.
/// </summary>
public class SettingsService(IServiceScopeFactory scopeFactory) : IAppSettings
{
    private readonly ConcurrentDictionary<string, string?> _cache = new();
    private readonly Lock _gate = new();

    // Bumped by every write. A read that started before a write may have fetched the old row, so it
    // only fills the cache if no write landed while it was querying.
    private long _version;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var version = Interlocked.Read(ref _version);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        try
        {
            var entry = await db.AppConfig.FirstOrDefaultAsync(c => c.Key == key, ct);
            lock (_gate)
            {
                if (_version == version)
                {
                    _cache.TryAdd(key, entry?.Value);
                }
            }
            return entry?.Value;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table"))
        {
            // Fresh DB, migrations haven't created the schema yet (e.g. pre-migrate backup).
            return null;
        }
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var entry = await db.AppConfig.FirstOrDefaultAsync(c => c.Key == key, ct);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (entry != null)
            {
                db.AppConfig.Remove(entry);
            }
        }
        else if (entry is null)
        {
            db.AppConfig.Add(new AppConfigEntry { Key = key, Value = value });
        }
        else
        {
            entry.Value = value;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            lock (_gate)
            {
                _version++;
                _cache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Forgets every cached value, for anything that wrote AppConfig behind this service.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _cache.Clear();
        }
    }
}
