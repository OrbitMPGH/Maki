namespace Maki.Data;

/// <summary>
/// Whose data the current <see cref="MakiDbContext"/> may see. Injected into the context and read by
/// the global query filters, so every user-owned table is scoped by construction rather than by each
/// call site remembering to add a <c>WHERE UserId = …</c>.
/// <para>
/// A fresh instance is <b>unrestricted</b> by default. Requests narrow it in exactly one place:
/// <c>CurrentUserMiddleware</c>, which runs before authorization and calls <see cref="SetUser"/> for
/// an authenticated caller or <see cref="SetNobody"/> for an anonymous one. Most non-request work,
/// including Quartz jobs, hosted services, and the download workers, keeps the default instead. The
/// scrobble tick is the exception: its per-user passes narrow a fresh scoped <c>DbContext</c> to one
/// user at a time via <see cref="SetUser"/>.
/// </para>
/// <para>
/// The default is deliberately <em>not</em> deny-all, and the reason is worth knowing before
/// "fixing" it: several singletons open a child scope while a request is in flight (recommendations,
/// the queued rating push), and a fire-and-forget continuation inherits the request's execution
/// context. A deny-all default would leave those reading an empty library intermittently, depending
/// on who happened to trigger them. Narrowing at the single point that knows the answer is both
/// safer and easier to audit than a default that is right for one of the two worlds.
/// </para>
/// <para>
/// A background job that wants one user's rows opens a fresh scoped <c>DbContext</c> and narrows
/// that instance's own scope, the way <c>UserSettingsStore</c> and the per-user loops in
/// <c>ScrobbleService</c> both do, rather than mutating a scope anything else shares.
/// </para>
/// </summary>
public sealed class DataScope
{
    /// <summary>Which user's rows the user-owned tables show. Meaningless while <see cref="Unrestricted"/>.</summary>
    public int UserId { get; private set; }

    /// <summary>Bypasses every filter. True until something narrows the scope.</summary>
    public bool Unrestricted { get; private set; } = true;

    /// <summary>Whether the series filter is satisfied by any root folder rather than a granted one.</summary>
    public bool AllRootFolders { get; private set; } = true;

    /// <summary>Narrows to one user and the root folders they were granted.</summary>
    public void SetUser(int userId, bool allRootFolders)
    {
        UserId = userId;
        AllRootFolders = allRootFolders;
        Unrestricted = false;
    }

    /// <summary>
    /// Narrows to nobody: no user-owned rows, no series. What an anonymous request gets, so an
    /// allow-anonymous endpoint cannot read library data even by accident.
    /// </summary>
    public void SetNobody() => SetUser(0, allRootFolders: false);
}
