using System.Collections.Concurrent;

namespace Maki.Api.Services;

/// <summary>
/// Spaces out the disk work of the 15-minute health refresh. Creating a probe file in every root
/// on every tick kept NAS disks from ever spinning down, so a directory that passed is trusted for
/// <see cref="WriteInterval"/>, and a failed one is probed again on every tick until it recovers.
/// A download or import that fails with an IO error calls <see cref="Expire"/>, so the next tick
/// probes everything again. The repair staging size is re-measured only while a health operation
/// can be writing it, after the operations changed, or once per <see cref="StagingInterval"/>.
/// </summary>
public sealed class HealthWriteProbe
{
    public static readonly TimeSpan WriteInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan StagingInterval = TimeSpan.FromHours(6);

    public static HealthWriteProbe Shared { get; } = new();

    private readonly ConcurrentDictionary<string, DateTime> _passed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, (DateTime At, string Fingerprint, long Bytes)> _staging = new();

    public bool WriteDue(string directory, DateTime nowUtc) =>
        !_passed.TryGetValue(directory, out var at) || at > nowUtc || nowUtc - at >= WriteInterval;

    public void RecordWrite(string directory, bool writable, DateTime nowUtc)
    {
        if (writable) _passed[directory] = nowUtc;
        else _passed.TryRemove(directory, out _);
    }

    /// <summary>The last staging size of a root, or null when it has to be measured again.</summary>
    public long? CachedStaging(int rootId, string fingerprint, bool operationsActive, DateTime nowUtc)
    {
        if (operationsActive || !_staging.TryGetValue(rootId, out var cached)) return null;
        if (cached.Fingerprint != fingerprint || cached.At > nowUtc || nowUtc - cached.At >= StagingInterval) return null;
        return cached.Bytes;
    }

    public void RecordStaging(int rootId, string fingerprint, long? bytes, DateTime nowUtc)
    {
        if (bytes is { } measured) _staging[rootId] = (nowUtc, fingerprint, measured);
        else _staging.TryRemove(rootId, out _);
    }

    public void Expire()
    {
        _passed.Clear();
        _staging.Clear();
    }
}
