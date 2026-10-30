namespace Maki.Core.Images;

/// <summary>
/// An async semaphore whose waiters take different amounts of one budget. First come, first
/// served: a heavy waiter at the head holds back lighter ones behind it, so a large page is never
/// starved by a stream of thumbnails.
/// </summary>
internal sealed class WeightedGate(int capacity)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<(int Weight, TaskCompletionSource Granted)> _waiters = new();
    private int _available = capacity;

    public async Task WaitAsync(int weight, CancellationToken ct)
    {
        TaskCompletionSource granted;
        LinkedListNode<(int Weight, TaskCompletionSource Granted)> node;
        lock (_lock)
        {
            if (_waiters.Count == 0 && _available >= weight)
            {
                _available -= weight;
                return;
            }

            granted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast((weight, granted));
        }

        await using var registration = ct.Register(() =>
        {
            lock (_lock)
            {
                if (node.List is null)
                {
                    return; // already granted
                }

                _waiters.Remove(node);
                Drain();
            }

            granted.TrySetCanceled(ct);
        });

        await granted.Task;
    }

    /// <summary>Grows or shrinks the budget. A shrink below what is taken holds new waiters until enough is released.</summary>
    public void Resize(int delta) => Release(delta);

    public void Release(int weight)
    {
        lock (_lock)
        {
            _available += weight;
            Drain();
        }
    }

    private void Drain()
    {
        while (_waiters.First is { } first && _available >= first.Value.Weight)
        {
            _waiters.RemoveFirst();
            _available -= first.Value.Weight;
            first.Value.Granted.TrySetResult();
        }
    }
}
