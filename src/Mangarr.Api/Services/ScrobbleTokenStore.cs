using Mangarr.Core.Entities;
using Mangarr.Core.Scrobbling;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>DB-backed tracker token persistence (singleton; trackers are singletons too).</summary>
public class ScrobbleTokenStore(IServiceScopeFactory scopeFactory) : IScrobbleTokenStore
{
    public async Task<ScrobbleToken?> GetAsync(string service, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        return await db.ScrobbleTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Service == service, ct);
    }

    public async Task SaveAsync(ScrobbleToken token, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var existing = await db.ScrobbleTokens.FirstOrDefaultAsync(t => t.Service == token.Service, ct);
        if (existing is null)
        {
            db.ScrobbleTokens.Add(token);
        }
        else
        {
            existing.AccessToken = token.AccessToken;
            existing.RefreshToken = token.RefreshToken;
            existing.ExpiresAt = token.ExpiresAt;
            existing.Username = token.Username;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string service, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        await db.ScrobbleTokens.Where(t => t.Service == service).ExecuteDeleteAsync(ct);
    }
}
