namespace Maki.Metadata.Ease;

/// <summary>
/// The knobs behind the EASE channel, a signed item-item table fitted on AniList reading lists
/// (<see cref="EaseScorer"/>). Measurement only: nothing in the app constructs an
/// <see cref="EaseCache"/>, and <see cref="Weight"/> ships at 0, so every result is what it was
/// before the channel existed. <c>distribution/CLAUDE.md</c> has the numbers.
/// </summary>
public sealed record EaseTuning
{
    public static readonly EaseTuning Default = new();

    /// <summary>
    /// The channel's coefficient in <c>EmbeddingMath.HybridScore</c>, against a score whose best
    /// candidate is 1. 0 switches the channel off entirely.
    /// </summary>
    public double Weight { get; init; }

    /// <summary>
    /// Whether negative weights count. On is what the research measured best: readers who finished
    /// a seed tend not to finish what it weighs negatively, and dropping that made picks more famous.
    /// </summary>
    public bool KeepNegative { get; init; } = true;

    /// <summary>Ceiling on rows the channel may push into the scoring pool per request.</summary>
    public int MaxInjected { get; init; } = 50;

    /// <summary>
    /// Normalized score a row needs before the channel may push it into the pool. Scoring is
    /// unaffected: a pooled row keeps its full term however small.
    /// </summary>
    public double MinInjectedScore { get; init; } = 0.6;
}
