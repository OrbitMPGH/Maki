namespace Maki.Core.Scrobbling;

/// <summary>
/// The forward-only push decision for one series on one tracker. When
/// <see cref="Write"/> is false only the local sync state is recorded.
/// </summary>
/// <param name="Write">Whether to send an update to the tracker.</param>
/// <param name="Chapter">Chapter progress to push / record.</param>
/// <param name="Volume">Volume progress to push / record.</param>
/// <param name="PushStatus">Status to send when writing.</param>
/// <param name="RecordStatus">Status to record in the local sync state.</param>
public record ScrobblePlan(
    bool Write, int Chapter, int Volume, ScrobbleStatus PushStatus, ScrobbleStatus RecordStatus);

/// <summary>
/// Pure decision logic for pushing progress to a tracker. Forward-only: the pushed
/// value is max(remote, kavita); remote progress is never lowered, completed entries
/// are never demoted, and user-set statuses (paused/dropped/planning) are only moved
/// back to reading when Kavita shows further reading progress.
/// </summary>
public static class ScrobblePlanner
{
    /// <param name="entry">The user's current entry on the tracker.</param>
    /// <param name="chapter">Highest fully-read chapter in Kavita (floored).</param>
    /// <param name="volume">Highest fully-read volume in Kavita (floored).</param>
    /// <param name="fallbackStatus">
    /// Status to list a not-yet-listed series under when there is no scrobbable
    /// progress (plan-to-read sync); null when that behavior is off.
    /// </param>
    public static ScrobblePlan Decide(
        RemoteEntry entry, int chapter, int volume, ScrobbleStatus? fallbackStatus = null)
    {
        if (chapter <= 0 && volume <= 0)
        {
            // No scrobbable progress: only add the series to the list if it isn't
            // there yet; never touch an existing entry.
            if (entry.Status is { } existing)
            {
                return new ScrobblePlan(false, entry.ProgressChapter, entry.ProgressVolume, existing, existing);
            }

            var listAs = fallbackStatus ?? ScrobbleStatus.PlanToRead;
            return new ScrobblePlan(true, 0, 0, listAs, listAs);
        }

        var newCh = Math.Max(chapter, entry.ProgressChapter);
        var newVol = Math.Max(volume, entry.ProgressVolume);

        var completed = false;
        if (entry.TotalChapters is > 0 && newCh >= entry.TotalChapters)
        {
            completed = true;
            newCh = entry.TotalChapters.Value;
        }
        else if (entry.TotalVolumes is > 0 && newVol >= entry.TotalVolumes && entry.TotalChapters is null)
        {
            completed = true;
        }

        if (entry.Status == ScrobbleStatus.Completed)
        {
            completed = true; // never demote a completed entry
        }

        var status = completed ? ScrobbleStatus.Completed : ScrobbleStatus.Reading;

        var noProgressChange = newCh == entry.ProgressChapter && newVol == entry.ProgressVolume;
        var statusUnchanged = entry.Status == status ||
                              (entry.Status == ScrobbleStatus.Other && status == ScrobbleStatus.Reading);
        if (entry.Status is { } current && noProgressChange && statusUnchanged)
        {
            return new ScrobblePlan(false, newCh, newVol, current, current);
        }

        if (entry.Status == ScrobbleStatus.Other && noProgressChange)
        {
            return new ScrobblePlan(false, newCh, newVol, ScrobbleStatus.Other, ScrobbleStatus.Other);
        }

        var pushStatus = status;
        if (entry.Status == ScrobbleStatus.Other && !completed)
        {
            // The user set a custom status (paused/dropped/planning) but read
            // further — move it back to reading since they clearly are.
            pushStatus = ScrobbleStatus.Reading;
        }

        return new ScrobblePlan(true, newCh, newVol, pushStatus, pushStatus);
    }
}
