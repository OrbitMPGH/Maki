namespace Maki.Core.Sources;

/// <summary>
/// The source lists the chapter but will not hand over its pages without a login or a purchase.
/// Retrying it on the same source cannot work, so the download moves to the next one at once.
/// </summary>
public sealed class ChapterRequiresPurchaseException(string message) : SourceErrorException(message);
