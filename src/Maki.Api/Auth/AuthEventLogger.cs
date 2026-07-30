using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <summary>
/// Appends to the security audit trail. Trimmed on write the same way the scrobble log is — an
/// exposed instance takes a steady stream of failed logins and this table would otherwise grow
/// without bound inside the user's <c>maki.db</c>.
/// </summary>
public class AuthEventLogger(MakiDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Deliberately larger than the scrobble log's 500: this is forensic data, and a burst of
    /// credential stuffing must not push the *interesting* rows (a permission change, a successful
    /// login from an unfamiliar address) out of the window before anyone looks.
    /// </summary>
    private const int MaxRows = 5000;

    public async Task LogAsync(
        AuthEventType type,
        string userName,
        int? userId = null,
        HttpContext? context = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        db.AuthEvents.Add(new AuthEvent
        {
            Timestamp = clock.GetUtcNow().UtcDateTime,
            Type = type,
            UserId = userId,
            UserName = userName,
            ClientIp = context?.Connection.RemoteIpAddress?.ToString(),
            // Truncated: this is an attacker-controlled header and there is no reason to store a
            // kilobyte of it per failed login.
            UserAgent = Truncate(context?.Request.Headers.UserAgent.FirstOrDefault(), 256),
            Detail = detail
        });

        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM AuthEvents WHERE Id NOT IN (SELECT Id FROM AuthEvents ORDER BY Id DESC LIMIT {MaxRows})", ct);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
