namespace Maki.Metadata.RecoGraph;

/// <summary>
/// Where the co-recommendation edge database lives. Constructed from AppPaths in the
/// API host (Maki.Metadata cannot reference Maki.Api).
/// <para>
/// This is <c>reco-edges.db</c>, the folded artifact, never <c>reco-graph.db</c> — that second file
/// is <c>distribution/fetch-reco-graph.cs</c>'s resumable working state, carries per-provider
/// directed rows plus fetch bookkeeping, and means nothing to the app.
/// </para>
/// </summary>
public record RecoGraphOptions(string DatabasePath, string StagingDirectory);
