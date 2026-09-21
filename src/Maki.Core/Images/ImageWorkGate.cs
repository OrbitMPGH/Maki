namespace Maki.Core.Images;

/// <summary>
/// Process-wide ceiling on how many images are being decoded at once.
///
/// <para>
/// A decoded page is uncompressed pixels, not the file on disk: ImageSharp's default
/// <c>Rgba32</c> makes a 2000×3000 scan ~24 MB, and a descramble holds two of them. Nothing used to
/// bound how many of those existed together, because the limits that do exist are about something
/// else — <c>PageDownloader</c> caps pages per chapter, the download worker caps chapters, the
/// reader's thumbnail endpoint caps nothing at all — and they multiply rather than compose. Raising
/// the chapter-concurrency setting therefore bought a proportional rise in peak RSS that no
/// image-facing code had asked for.
/// </para>
///
/// <para>
/// Static rather than injected because it models a machine limit, not a policy: two callers in
/// different projects have to queue behind the same permits or the ceiling is not one. Gated work
/// must never gate again inside itself — the permits are not reentrant, and every current caller is
/// a leaf.
/// </para>
/// </summary>
public static class ImageWorkGate
{
    /// <summary>
    /// Half the cores, floored at 2 so a single-core container still makes progress and capped at 4
    /// because past that the limit is memory rather than CPU: four concurrent decodes of a large
    /// page is already ~100 MB of live pixels.
    /// </summary>
    private static readonly SemaphoreSlim Permits =
        new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    public static async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await Permits.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            Permits.Release();
        }
    }

    public static async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        await Permits.WaitAsync(ct);
        try
        {
            await work();
        }
        finally
        {
            Permits.Release();
        }
    }
}
