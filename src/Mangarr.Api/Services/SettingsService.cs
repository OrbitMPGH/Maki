using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>Singleton IAppSettings over the AppConfig table (short-lived scopes per read).</summary>
public class SettingsService(IServiceScopeFactory scopeFactory) : IAppSettings
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var entry = await db.AppConfig.FirstOrDefaultAsync(c => c.Key == key, ct);
        return entry?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
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

        await db.SaveChangesAsync(ct);
    }
}
