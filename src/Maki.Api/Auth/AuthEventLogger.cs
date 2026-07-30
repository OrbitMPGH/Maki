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
            // Truncated for the same reason the user agent is: on a failed login this is whatever
            // the caller typed into the form, and there is no username worth 30 MB of maki.db.
            UserName = Truncate(userName, 256) ?? string.Empty,
            ClientIp = context?.Connection.RemoteIpAddress?.ToString(),
            // Truncated: this is an attacker-controlled header and there is no reason to store a
            // kilobyte of it per failed login.
            UserAgent = Truncate(context?.Request.Headers.UserAgent.FirstOrDefault(), 256),
            Detail = detail
        });

        await db.SaveChangesAsync(ct);

        // Trimmed by id range rather than "NOT IN (SELECT … ORDER BY Id DESC LIMIT n)", which is a
        // scan plus an anti-join over the whole table on *every* write — including every failed
        // login, which is exactly the traffic a credential-stuffing burst produces and exactly what
        // the cap exists to survive. MAX(Id) is a single seek to the end of the primary key, and the
        // delete then touches only the rows actually falling out of the window: nothing at all until
        // the table is full, one row per insert afterwards.
        //
        // This keeps precisely the last MaxRows because the ids have no gaps to widen the range:
        // rows are only ever appended, and the only delete is this one, which removes a contiguous
        // prefix. (AuthEvent deliberately has no FK to MakiUser, so deleting an account does not
        // punch holes in it either.)
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM AuthEvents WHERE Id <= (SELECT MAX(Id) FROM AuthEvents) - {MaxRows}", ct);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
