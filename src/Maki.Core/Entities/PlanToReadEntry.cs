using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// A catalogue title the reader saved to their "Want to read" list. <see cref="Title"/> and
/// <see cref="CoverUrl"/> are resolved from the dump at save time, never taken from the client, and
/// kept as a snapshot so the list still renders if the dump is removed later.
/// </summary>
public class PlanToReadEntry : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = "mangabaka";
    public long ProviderId { get; set; }
    public string Title { get; set; } = "";
    public string? CoverUrl { get; set; }

    /// <summary>
    /// The dump's rating at save time, so the ceiling is enforced in SQL and still applies with no
    /// dump. Null reads as allowed, the same as a library row with no rating.
    /// </summary>
    public string? ContentRating { get; set; }

    /// <summary>Where the save came from: <c>taste</c>, <c>trending</c> or <c>manual</c>.</summary>
    public string Origin { get; set; } = "";
    public DateTime AddedAtUtc { get; set; }
}
