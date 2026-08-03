using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// DB-backed tracker token persistence, keyed <c>(UserId, Service)</c> (singleton; trackers are
/// singletons too).
/// <para>
/// The user id is filtered explicitly on every query even though <c>ScrobbleTokens</c> carries a
/// global filter. The scopes opened here are fresh and therefore unrestricted, which is what lets the
/// background scrobble tick read a token for a user who is not making the request — so the narrowing
/// has to be in the predicate, not left to the model.
/// </para>
/// </summary>
public class ScrobbleTokenStore(IServiceScopeFactory scopeFactory) : IScrobbleTokenStore
{
    public async Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.ScrobbleTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == userId && t.Service == service, ct);
    }

    public async Task SaveAsync(ScrobbleToken token, CancellationToken ct = default)
    {
        if (token.UserId == 0)
        {
            // A token with no owner would be invisible to every reader and would collide with the
            // next unowned one on the composite key. Fail loudly rather than write it.
            throw new ArgumentException("A scrobble token must name the user it belongs to", nameof(token));
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var existing = await db.ScrobbleTokens
            .FirstOrDefaultAsync(t => t.UserId == token.UserId && t.Service == token.Service, ct);
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

    public async Task DeleteAsync(int userId, string service, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await db.ScrobbleTokens
            .Where(t => t.UserId == userId && t.Service == service)
            .ExecuteDeleteAsync(ct);
    }
}
