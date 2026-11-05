namespace Maki.Core.Quality;

/// <summary>
/// Whether a source has been delivering. Quality scoring measures files that arrived; a source that
/// lists chapters it cannot serve never produces one, so it keeps its place at the top and every
/// chapter spends its failures there before moving on.
/// </summary>
public static class SourceReliability
{
    /// <summary>Failures below this say nothing yet: one bad night is not a verdict.</summary>
    public const int MinFailures = 3;

    /// <summary>A source is unreliable once it fails this many times for every download that worked.</summary>
    public const int FailuresPerSuccess = 3;

    public readonly record struct Outcome(int Completed, int Failed)
    {
        public int Attempts => Completed + Failed;
    }

    public static bool IsUnreliable(Outcome outcome) =>
        outcome.Failed >= MinFailures && outcome.Failed >= outcome.Completed * FailuresPerSuccess;

    /// <summary>Moves unreliable items behind the rest, keeping the given order inside each group.</summary>
    public static IReadOnlyList<T> Demote<T>(IReadOnlyList<T> ordered, Func<T, Outcome> outcomeOf)
    {
        var unreliable = ordered.Where(item => IsUnreliable(outcomeOf(item))).ToList();
        return unreliable.Count == 0 ? ordered : [.. ordered.Except(unreliable), .. unreliable];
    }
}
