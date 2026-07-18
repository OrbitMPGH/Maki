namespace Maki.Metadata.Embedding;

/// <summary>Point-in-time view of the embedding index pass, for the settings UI.</summary>
public record EmbeddingIndexSnapshot(
    bool Running,
    string Phase,
    int Embedded,
    int Scanned,
    int? RecommendableTotal,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int LastEmbedded,
    int LastSkipped,
    string? LastError);

/// <summary>
/// Thread-safe live status of the background embedding indexer, shared between the indexer
/// (writer) and the settings endpoint (reader). Phases: "idle", "preparing" (model download /
/// warm-up), "indexing".
/// </summary>
public class EmbeddingIndexStatus
{
    private readonly object _gate = new();
    private bool _running;
    private string _phase = "idle";
    private int _embedded;
    private int _scanned;
    private int? _total;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
    private int _lastEmbedded;
    private int _lastSkipped;
    private string? _lastError;

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public void Begin()
    {
        lock (_gate)
        {
            _running = true;
            _phase = "preparing";
            _embedded = 0;
            _scanned = 0;
            _startedAt = DateTime.UtcNow;
            _finishedAt = null;
            _lastError = null;
        }
    }

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            _phase = phase;
        }
    }

    public void SetTotal(int total)
    {
        lock (_gate)
        {
            _total = total;
        }
    }

    public void Report(int scanned, int embedded)
    {
        lock (_gate)
        {
            _scanned = scanned;
            _embedded = embedded;
        }
    }

    public void End(int embedded, int skipped, string? error)
    {
        lock (_gate)
        {
            _running = false;
            _phase = "idle";
            _embedded = embedded;
            _lastEmbedded = embedded;
            _lastSkipped = skipped;
            _finishedAt = DateTime.UtcNow;
            _lastError = error;
        }
    }

    public EmbeddingIndexSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new EmbeddingIndexSnapshot(
                _running, _phase, _embedded, _scanned, _total,
                _startedAt, _finishedAt, _lastEmbedded, _lastSkipped, _lastError);
        }
    }
}
