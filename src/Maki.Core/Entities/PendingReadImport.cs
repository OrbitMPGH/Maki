using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// Chapter numbers a reader brought in from another app (a Mihon backup) that still have to be
/// marked read, because the series had no chapter rows yet when they were imported. One row per
/// series and reader; a second import merges its numbers into the first. Applied and removed by
/// <c>PendingReadImportService</c> once the series has chapters.
/// </summary>
public class PendingReadImport : IUserOwned
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int UserId { get; set; }

    /// <summary>JSON array of decimal chapter numbers, compared exactly against <see cref="Chapter.Number"/>.</summary>
    public string NumbersJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }
}
