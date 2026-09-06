using Maki.Core.Entities;

namespace Maki.Api.Services;

public static class HealthTransitions
{
    public static bool IsIssue(string status) => status is "warning" or "error" or "unavailable";

    /// <summary>
    /// Checks that still want someone's attention: an unresolved status nobody has acknowledged.
    /// <para>
    /// An expression rather than a method so EF can translate it, and one definition rather than a
    /// copy per caller so the header badge and anything else counting problems cannot drift apart
    /// on what "acknowledged" means.
    /// </para>
    /// </summary>
    public static readonly System.Linq.Expressions.Expression<Func<HealthCheckRecord, bool>> Unattended =
        row => !row.Acknowledged && (row.Status == "warning" || row.Status == "error" || row.Status == "unavailable");

    /// <summary>Updates persisted state and returns whether to announce a meaningful transition.</summary>
    public static bool Observe(HealthCheckRecord row, string status, bool connectivity, DateTime now)
    {
        var bad = IsIssue(status);
        row.ConsecutiveFailures = bad ? Math.Min(row.ConsecutiveFailures + 1, 1000) : 0;
        if (row.Status != status) { row.ChangedAt = now; row.Acknowledged = false; }
        row.Status = status;
        row.CheckedAt = now;
        if (connectivity && bad && row.ConsecutiveFailures < 2) return false;
        if (row.NotifiedStatus == status) return false;
        var announce = bad || IsIssue(row.NotifiedStatus);
        row.NotifiedStatus = status;
        return announce;
    }
}
