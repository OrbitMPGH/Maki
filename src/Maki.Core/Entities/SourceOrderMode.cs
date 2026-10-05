namespace Maki.Core.Entities;

/// <summary>How a series' source mappings are ordered when a chapter is downloaded.</summary>
public enum SourceOrderMode
{
    /// <summary>By <see cref="SourceMapping.Priority"/>, as the user set it.</summary>
    Manual = 0,

    /// <summary>
    /// By the series' upgrade profile: tier first, then the score its recent chapters measured,
    /// with <see cref="SourceMapping.Priority"/> breaking ties.
    /// </summary>
    Quality = 1
}
