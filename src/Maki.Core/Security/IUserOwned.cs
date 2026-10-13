namespace Maki.Core.Security;

/// <summary>
/// A row that belongs to exactly one user. Implementing this means the DbContext stamps the field on
/// insert when the caller left it at 0 and a user is in scope, and that the entity must declare its own
/// query filter on <see cref="UserId"/> in <c>OnModelCreating</c>; <c>DataModelTests</c> fails when one is missing.
/// <para>
/// The stamp is a backstop, not the mechanism — every write path should still say whose row it is,
/// because a background job runs unrestricted and gets no stamp. What it buys is the failure mode: a
/// forgotten assignment produces a row owned by user 0, which the query filter then hides from
/// everybody. Visible-to-nobody is a bug someone reports; visible-to-everybody is a breach nobody
/// notices.
/// </para>
/// <para>
/// Deliberately not implemented by <c>StatsEvent</c>: its <c>UserId</c> is nullable because null
/// means "library-wide", and a stamp would quietly convert those into one user's private history.
/// </para>
/// </summary>
public interface IUserOwned
{
    int UserId { get; set; }
}
