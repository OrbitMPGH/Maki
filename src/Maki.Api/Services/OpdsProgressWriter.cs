using System.Threading.Channels;
using Maki.Core.Opds;
using Maki.Data;

namespace Maki.Api.Services;

/// <summary>
/// Records OPDS page fetches as reading progress off the response path.
/// <para>
/// A reading app prefetches several pages at once, and writing each one before sending its bytes
/// queued every page behind SQLite's single writer. The controller now only hands the fetch over
/// here; one worker writes it, and fetches that arrive for the same user and chapter while a write
/// is pending fold into one, keeping the highest page.
/// </para>
/// <para>
/// Folding must not change what completes a chapter. <see cref="OpdsProgressPolicy"/> refuses to
/// complete from a last-page fetch when no progress row exists yet, because readers prefetch that
/// page to size their page bar. Written one at a time, a last-page fetch that followed any earlier
/// fetch found the row that fetch created, so the folded entry remembers that case and the write
/// treats it as though the row existed.
/// </para>
/// </summary>
public sealed class OpdsProgressWriter(IServiceScopeFactory scopes, ILogger<OpdsProgressWriter> logger)
    : BackgroundService
{
    private readonly record struct Key(int UserId, int ChapterId);

    private sealed class Pending
    {
        public int Page { get; set; }
        public bool AllRootFolders { get; set; }
        public bool LastPageAfterEarlierFetch { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<Key, Pending> _pending = [];
    private readonly Channel<Key> _ready = Channel.CreateUnbounded<Key>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(int userId, bool allRootFolders, int chapterId, int page, int pageCount)
    {
        var key = new Key(userId, chapterId);
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var pending))
            {
                pending.LastPageAfterEarlierFetch |= page >= pageCount - 1;
                pending.Page = Math.Max(pending.Page, page);
                pending.AllRootFolders = allRootFolders;
                return;
            }

            _pending[key] = new Pending { Page = page, AllRootFolders = allRootFolders };
        }

        _ready.Writer.TryWrite(key);
    }

    /// <summary>Writes everything queued so far.</summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        while (_ready.Reader.TryRead(out var key))
        {
            await WriteAsync(key, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var key in _ready.Reader.ReadAllAsync(stoppingToken))
            {
                await WriteAsync(key, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Whatever arrived during shutdown is still somebody's reading.
        await FlushAsync(CancellationToken.None);
    }

    private async Task WriteAsync(Key key, CancellationToken ct)
    {
        Pending? pending;
        lock (_lock)
        {
            if (!_pending.Remove(key, out pending))
            {
                return;
            }
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DataScope>().SetUser(key.UserId, pending.AllRootFolders);
            var reader = scope.ServiceProvider.GetRequiredService<ReaderService>();

            var slice = await reader.SliceAsync(key.ChapterId, ct);
            if (slice is null)
            {
                return;
            }

            var existing = await reader.ProgressAsync(key.ChapterId, ct);
            var completed = OpdsProgressPolicy.CompletionFor(
                existing is not null || pending.LastPageAfterEarlierFetch, pending.Page, slice.PageCount);
            // No reading time: a page fetch says a page was asked for, not that anybody was
            // looking at it, and readers that prefetch would bill a whole chapter in one burst.
            await reader.SaveProgressAsync(slice, pending.Page, completed, ReaderService.TimeReport.None, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "OPDS progress write failed for chapter {ChapterId} page {Page}",
                key.ChapterId, pending.Page);
        }
    }
}
