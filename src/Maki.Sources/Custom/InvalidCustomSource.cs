using Maki.Core.Sources;
using Maki.Core.Sources.Custom;

namespace Maki.Sources.Custom;

/// <summary>
/// Stands in for a stored definition this build can't use (a newer schema after a downgrade, a rule
/// tightened since it was saved). Registering nothing would make every linked series skip the source
/// silently; this keeps it in Manage sources as "Not working" and fails every call with the reason.
/// </summary>
public sealed class InvalidCustomSource(string id, string displayName, string baseUrl, string reason) : ISource
{
    public string Id { get; } = id;
    public string Reason { get; } = reason;

    public string Name => CustomSourceDefinition.NamePrefix + Id;
    public string DisplayName { get; } = displayName;
    public string BaseUrl { get; } = baseUrl;
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public SourceKind Kind => SourceKind.Scanlator;

    private SourceErrorException Broken() => new($"{DisplayName}: the custom source definition is invalid ({Reason})");

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) => throw Broken();
    public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default) => throw Broken();

    public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default) => throw Broken();

    public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) => throw Broken();
}
