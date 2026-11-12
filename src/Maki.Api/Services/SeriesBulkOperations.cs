using System.Collections.Concurrent;
using System.Threading.Channels;
using Maki.Core.Security;
using Maki.Data;

namespace Maki.Api.Services;

public enum SeriesBulkAction
{
    Refresh,
    Rescan,
    RefreshMetadata,
    SearchMissing,
    Rename,
    Move,
}

/// <summary>
/// Who queued a bulk operation, captured at request time. Every series runs in a fresh scope set up
/// as this caller, so root-folder scoping, permission checks and the error language match the
/// single endpoint the client used to call once per series.
/// </summary>
public sealed record SeriesBulkCaller(
    int UserId,
    string UserName,
    MakiPermission Permissions,
    bool AllRootFolders,
    IReadOnlySet<int> RootFolderIds,
    string MaxContentRating,
    bool ScopeUnrestricted,
    int ScopeUserId,
    bool ScopeAllRootFolders,
    string Locale)
{
    public static SeriesBulkCaller From(ICurrentUser user, DataScope scope, string locale) => new(
        user.UserId, user.UserName, user.Permissions, user.AllRootFolders, user.RootFolderIds.ToHashSet(),
        user.MaxContentRating, scope.Unrestricted, scope.UserId, scope.AllRootFolders, locale);
}

public sealed record SeriesBulkFailure(int SeriesId, string? Title, string Error);

public sealed record SeriesBulkStatus(
    Guid OperationId,
    string Action,
    string State,
    int Total,
    int Done,
    int Succeeded,
    int Failed,
    IReadOnlyList<SeriesBulkFailure> Failures);

public sealed class SeriesBulkOperation
{
    private readonly Lock _lock = new();
    private readonly List<SeriesBulkFailure> _failures = [];
    private int _done;
    private int _succeeded;
    private string _state = "queued";

    public required Guid Id { get; init; }
    public required SeriesBulkAction Action { get; init; }
    public required SeriesBulkCaller Caller { get; init; }
    public required IReadOnlyList<int> SeriesIds { get; init; }
    public int? RootFolderId { get; init; }
    public bool MoveFiles { get; init; }
    public CancellationTokenSource Cancel { get; } = new();
    public DateTime? FinishedAt { get; private set; }

    public bool IsFinished
    {
        get
        {
            lock (_lock)
            {
                return FinishedAt is not null;
            }
        }
    }

    public void Started()
    {
        lock (_lock)
        {
            _state = "running";
        }
    }

    public void Record(int seriesId, string? title, string? error)
    {
        lock (_lock)
        {
            _done++;
            if (error is null)
            {
                _succeeded++;
            }
            else
            {
                _failures.Add(new SeriesBulkFailure(seriesId, title, error));
            }
        }
    }

    public void Finish(bool cancelled)
    {
        lock (_lock)
        {
            _state = cancelled ? "cancelled" : "finished";
            FinishedAt = DateTime.UtcNow;
        }
    }

    public SeriesBulkStatus Snapshot(int maxFailures = int.MaxValue)
    {
        lock (_lock)
        {
            return new SeriesBulkStatus(Id, Action.ToString(), _state, SeriesIds.Count, _done, _succeeded,
                _failures.Count, _failures.Take(maxFailures).ToList());
        }
    }
}

/// <summary>
/// The Library's bulk bar used to call a per-series endpoint once per selected series, so 2,000
/// selected meant 2,000 requests from the browser and a tab that had to stay open. The bulk
/// endpoints validate the ids, queue one operation here and return; <see cref="SeriesBulkWorker"/>
/// works through it one series at a time and pushes progress to the caller.
/// <para>
/// Two lanes, each one operation at a time: file operations (move, rename) and everything else.
/// A long refresh of the whole library must not hold back a rename, and two file operations must
/// never run at once. Operations live in memory only; a restart drops whatever was still queued.
/// </para>
/// </summary>
public sealed class SeriesBulkOperations
{
    /// <summary>Largest id list one request may carry. The client sends bigger selections in chunks.</summary>
    public const int MaxSeries = 5000;

    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, SeriesBulkOperation> _operations = new();
    private readonly Channel<SeriesBulkOperation> _files = Channel.CreateUnbounded<SeriesBulkOperation>();
    private readonly Channel<SeriesBulkOperation> _work = Channel.CreateUnbounded<SeriesBulkOperation>();

    public static bool IsFileOperation(SeriesBulkAction action) =>
        action is SeriesBulkAction.Move or SeriesBulkAction.Rename;

    public SeriesBulkOperation Enqueue(SeriesBulkAction action, IReadOnlyList<int> seriesIds, SeriesBulkCaller caller,
        int? rootFolderId = null, bool moveFiles = false)
    {
        Prune();
        var operation = new SeriesBulkOperation
        {
            Id = Guid.NewGuid(),
            Action = action,
            Caller = caller,
            SeriesIds = seriesIds,
            RootFolderId = rootFolderId,
            MoveFiles = moveFiles,
        };
        _operations[operation.Id] = operation;
        (IsFileOperation(action) ? _files : _work).Writer.TryWrite(operation);
        return operation;
    }

    /// <summary>The caller's own operation, or null. Nobody else learns another user's operation exists.</summary>
    public SeriesBulkOperation? Get(Guid id, int userId)
    {
        Prune();
        return _operations.TryGetValue(id, out var operation) && operation.Caller.UserId == userId ? operation : null;
    }

    public bool TryCancel(Guid id, int userId)
    {
        if (Get(id, userId) is not { } operation)
        {
            return false;
        }

        operation.Cancel.Cancel();
        return true;
    }

    public ValueTask<SeriesBulkOperation> ReadAsync(bool files, CancellationToken ct) =>
        (files ? _files : _work).Reader.ReadAsync(ct);

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - KeepFinished;
        foreach (var (id, operation) in _operations)
        {
            if (operation.FinishedAt is { } finished && finished < cutoff)
            {
                _operations.TryRemove(id, out _);
            }
        }
    }
}
