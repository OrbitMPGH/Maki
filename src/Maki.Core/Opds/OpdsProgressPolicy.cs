namespace Maki.Core.Opds;

/// <summary>
/// How a streamed page maps onto reading progress.
/// <para>
/// OPDS-PSE has no write-back call, so fetching a page <em>is</em> the only progress signal a
/// reading app produces. That makes the page request equivalent to the built-in reader's own
/// position save — with one exception, which is the whole reason this lives in its own testable
/// type rather than inline in the controller.
/// </para>
/// </summary>
public static class OpdsProgressPolicy
{
    /// <summary>
    /// The <c>completed</c> argument to pass to the reader's progress save: null to apply the
    /// reader's normal rule ("at the last page means finished"), false to override it.
    /// <para>
    /// The override exists because several readers fetch the <em>final</em> page of a chapter up
    /// front, before showing anything, to size their page bar. Under the normal rule that single
    /// request would mark the chapter read before a word of it had been — and completion is
    /// sticky, feeds the high-water mark, and fires a read event at the connected trackers, so
    /// there is nothing to undo it. A chapter that already has a progress row has been genuinely
    /// opened, so from then on the normal rule applies again.
    /// </para>
    /// </summary>
    public static bool? CompletionFor(bool hasProgressRow, int page, int pageCount) =>
        !hasProgressRow && page >= pageCount - 1 ? false : null;
}
