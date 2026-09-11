using System.Globalization;
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
/// The cost of being wrong is a reload on the next typed query, a second or two once. Set
/// <c>MAKI_EMBED_IDLE_MINUTES=0</c> to keep the session loaded forever, which is the old behaviour
/// and what a machine with RAM to spare and a search-heavy household wants.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class EmbedderIdleUnloadJob(TextEmbedder embedder) : IJob
{
    public static readonly JobKey Key = new("embedder-idle-unload");

    public const string IdleMinutesVariable = "MAKI_EMBED_IDLE_MINUTES";

    private const int DefaultIdleMinutes = 15;

    public Task Execute(IJobExecutionContext context)
    {
        var minutes = Resolve(Environment.GetEnvironmentVariable(IdleMinutesVariable));
        if (minutes > 0)
        {
            embedder.ReleaseIfIdle(TimeSpan.FromMinutes(minutes));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Minutes of idleness before the session goes. Zero disables the unload; anything unparseable
    /// or negative falls back to the default rather than being read as "never", since a typo in an
    /// environment variable should not silently restore a quarter-gigabyte of residency.
    /// </summary>
    internal static int Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultIdleMinutes;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= 0
                ? minutes
                : DefaultIdleMinutes;
    }
}
