using Maki.Core.Io;
using Maki.Metadata.Embedding;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Unloads the ONNX embedding session once nothing has used it for a while.
///
/// <para>
/// The default model is ~240 MB resident and used to stay that way for the rest of the process once
/// anything touched it, which on a typical instance is one natural-language search. Almost nothing
/// needs it: recommendations score vectors the index already holds, and local indexing only runs
/// when series are added. So the steady state was a quarter of a gigabyte held for a feature the
/// server was not using.
/// </para>
///
/// <para>
/// The cost of being wrong is a reload on the next typed query, a second or two once, which is why
/// the window is five minutes rather than the fifteen it started at: about 170 MB held for a
/// reload that cheap was the wrong trade. Set
/// <c>MAKI_EMBED_IDLE_MINUTES=0</c> to keep the session loaded forever, which is the old behaviour
/// and what a machine with RAM to spare and a search-heavy household wants.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class EmbedderIdleUnloadJob(TextEmbedder embedder) : IJob
{
    public static readonly JobKey Key = new("embedder-idle-unload");

    public const string IdleMinutesVariable = "MAKI_EMBED_IDLE_MINUTES";

    private const int DefaultIdleMinutes = 5;

    public Task Execute(IJobExecutionContext context)
    {
        var minutes = Resolve(Environment.GetEnvironmentVariable(IdleMinutesVariable));
        if (minutes > 0 && embedder.ReleaseIfIdle(TimeSpan.FromMinutes(minutes)))
        {
            NativeHeap.Trim();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Minutes of idleness before the session goes. Zero disables the unload; anything unparseable
    /// or negative falls back to the default rather than being read as "never", since a typo in an
    /// environment variable should not silently restore a quarter-gigabyte of residency.
    /// </summary>
    internal static int Resolve(string? value) => IdleWindow.Resolve(value, DefaultIdleMinutes);
}
