using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <summary>
/// Answers "would this change leave the instance with no usable administrator?".
/// <para>
/// Its own class, and injected rather than inlined into the controller, because the state it prevents
/// is unrecoverable through the UI: with zero usable admins nobody can reach settings, root folders or
/// user management ever again, and the only fix is editing <c>maki.db</c> by hand. That makes it worth
/// testing directly, which a private controller method is not.
/// </para>
/// </summary>
public class AdminGuard(MakiDbContext db)
{
    // The check and the write that follows it are two statements, so two concurrent demotions could
    // each see the other admin and both commit. Every check-then-save that can remove an admin holds
    // this across both.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate.Release();
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is the only account that can currently administer the
    /// instance — meaning demoting, disabling or deleting it must be refused.
    /// <para>
    /// "Usable" excludes disabled and never-claimed accounts on purpose: a second admin row that
    /// cannot sign in is not a way back in, so counting it would let the real admin lock everyone out.
    /// </para>
    /// </summary>
    public async Task<bool> IsLastAdminAsync(int userId, CancellationToken ct) =>
        !await db.Users.AnyAsync(
            u => u.Id != userId
                 && !u.Disabled
                 && !u.PendingSetup
                 && (u.Permissions & MakiPermission.Admin) != 0,
            ct);
}
