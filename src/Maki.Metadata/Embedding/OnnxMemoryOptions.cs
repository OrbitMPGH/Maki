using Microsoft.ML.OnnxRuntime;

namespace Maki.Metadata.Embedding;

/// <summary>
/// CPU session settings shared by the text embedder and the page upscaler, chosen for a small
/// resident session.
///
/// <para>
/// The CPU arena keeps the largest working set a session has ever run until it is disposed.
/// Measured on Windows with the shipped models (private bytes, identical outputs): the embedder held
/// 670 MB after one 16 x 256 batch with the arena and 130 MB without it, and SPAN-S held 1,081 MB
/// after six tiles against 21 MB. Query-sized embeds took the same 5 to 7 ms either way.
/// </para>
///
/// <para>
/// The memory pattern pre-plans one block per input shape. It pays off only for a fixed shape:
/// upscale tiles kept their 220 ms with it and took 320 ms without, while the embedder's shapes
/// change with every batch. Initializers come from the plain allocator, which needs the fixed glibc
/// mmap threshold the Docker image sets, or the separate allocations fragment glibc's heap instead.
/// </para>
/// </summary>
public static class OnnxMemoryOptions
{
    public static SessionOptions Create(int intraOpThreads, bool fixedShapes)
    {
        var options = new SessionOptions
        {
            EnableCpuMemArena = false,
            EnableMemoryPattern = fixedShapes,
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.use_device_allocator_for_initializers", "1");
        return options;
    }
}
