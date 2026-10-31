using Maki.Core.Io;

namespace Maki.Core.Images;

/// <summary>
/// Process-wide ceiling on how many images are being decoded at once.
///
/// <para>
/// A decoded page is uncompressed pixels, not the file on disk: RGBA makes a 2000×3000 scan
/// ~24 MB, and a descramble holds two of them. Nothing used to bound how many of those existed together, because the limits that do exist are about something
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
///
/// <para>
/// Two limits, both taken: a count of concurrent jobs, and a budget of
/// <see cref="BudgetMegapixels"/> shared by everything decoded at once. The count alone let four
/// 40 MP pages (about 160 MB of pixels each) decode together during a conversion pass. Callers that
/// decode a whole page pass its weight (<see cref="Weigh(int, int)"/>); a thumbnail decoded with
/// shrink-on-load costs the default of one.
/// </para>
/// </summary>
public static class ImageWorkGate
{
    /// <summary>
    /// Half the cores, floored at 2 so a single-core container still makes progress and capped at 4
    /// because past that the limit is memory rather than CPU: four concurrent decodes of a large
    /// page is already ~100 MB of live pixels.
    /// </summary>
    public static readonly int Parallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    private static readonly SemaphoreSlim Permits = new(Parallelism);

    /// <summary>About 256 MB of RGBA pixels in flight at most, or one page larger than that on its own.</summary>
    public const int BudgetMegapixels = 64;

    private static readonly WeightedGate Pixels = new(BudgetMegapixels);

    /// <summary>
    /// Page conversion encodes count against their own limit instead of <see cref="Parallelism"/>, so
    /// an admin can give the pass more cores than reading needs, and a reader's decode never queues
    /// behind seconds of AV1. They still take the pixel budget, which is what bounds memory.
    /// </summary>
    private static readonly WeightedGate Conversions = new(Parallelism);

    private static int _conversionLimit = Parallelism;

    /// <summary>The highest conversion limit an admin can set.</summary>
    public static int MaxConversionLimit => Math.Max(Environment.ProcessorCount, 1);

    /// <summary>How many conversion encodes run at once.</summary>
    public static int ConversionLimit => Volatile.Read(ref _conversionLimit);

    /// <summary>Null restores the default of <see cref="Parallelism"/>. Encodes already running finish first.</summary>
    public static void SetConversionLimit(int? limit)
    {
        var next = Math.Clamp(limit ?? Parallelism, 1, MaxConversionLimit);
        var previous = Interlocked.Exchange(ref _conversionLimit, next);
        Conversions.Resize(next - previous);
    }

    /// <summary>A page conversion encode: one of <see cref="ConversionLimit"/>, plus its pixel weight.</summary>
    public static async Task<T> RunConversionAsync<T>(Func<T> work, int megapixels, CancellationToken ct)
    {
        megapixels = Math.Clamp(megapixels, 1, BudgetMegapixels);
        await Conversions.WaitAsync(1, ct);
        try
        {
            await Pixels.WaitAsync(megapixels, ct);
        }
        catch
        {
            Conversions.Release(1);
            throw;
        }

        Interlocked.Increment(ref _active);
        try
        {
            return work();
        }
        finally
        {
            Conversions.Release(1);
            Pixels.Release(megapixels);
            Left();
        }
    }

    /// <summary>
    /// How long the gate has to stay empty before the decoders' freed buffers go back to the
    /// kernel. Long enough that a reader paging through a chapter or a scan between files never
    /// pays for it, short enough that a finished burst does not leave its heap behind for hours.
    /// </summary>
    private static readonly TimeSpan TrimAfter = TimeSpan.FromMinutes(2);

    private static int _active;

    private static readonly Timer IdleTrim = new(_ =>
    {
        if (Volatile.Read(ref _active) == 0)
        {
            NativeHeap.Trim();
        }
    });

    /// <summary>Megapixels started, at least one and at most the whole budget.</summary>
    public static int Weigh(int width, int height) =>
        (int)Math.Clamp(((long)width * height + 999_999) / 1_000_000, 1, BudgetMegapixels);

    /// <summary>The weight of decoding <paramref name="bytes"/>, read from the header; one when the header does not parse.</summary>
    public static int Weigh(byte[] bytes)
    {
        try
        {
            var header = ImageCodec.Identify(bytes);
            return Weigh(header.Width, header.Height);
        }
        catch (Exception)
        {
            // The decode itself reports what is wrong with the bytes.
            return 1;
        }
    }

    public static Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default) =>
        RunAsync(work, 1, ct);

    public static async Task<T> RunAsync<T>(Func<Task<T>> work, int megapixels, CancellationToken ct = default)
    {
        megapixels = Math.Clamp(megapixels, 1, BudgetMegapixels);
        await EnterAsync(megapixels, ct);
        try
        {
            return await work();
        }
        finally
        {
            Exit(megapixels);
        }
    }

    public static async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        await EnterAsync(1, ct);
        try
        {
            await work();
        }
        finally
        {
            Exit(1);
        }
    }

    private static async Task EnterAsync(int megapixels, CancellationToken ct)
    {
        await Pixels.WaitAsync(megapixels, ct);
        try
        {
            await Permits.WaitAsync(ct);
        }
        catch
        {
            Pixels.Release(megapixels);
            throw;
        }

        Interlocked.Increment(ref _active);
    }

    private static void Exit(int megapixels)
    {
        Permits.Release();
        Pixels.Release(megapixels);
        Left();
    }

    private static void Left()
    {
        if (Interlocked.Decrement(ref _active) == 0)
        {
            // Re-armed by every last release, so it fires once the burst is over, not during it.
            IdleTrim.Change(TrimAfter, Timeout.InfiniteTimeSpan);
        }
    }
}
