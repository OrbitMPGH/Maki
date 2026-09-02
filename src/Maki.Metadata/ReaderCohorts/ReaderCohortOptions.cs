namespace Maki.Metadata.ReaderCohorts;

/// <summary>
/// Where the reader-cohort database lives. Constructed from AppPaths in the API host
/// (Maki.Metadata cannot reference Maki.Api).
/// <para>
/// This is <c>reader-cohorts.db</c>, the group aggregate, never <c>coread-graph.db</c> — that
/// second file is <c>distribution/fetch-coread-graph.cs</c>'s working state and holds
/// <c>user_entry</c>, which is per-user reading data and must never leave the machine that fetched
/// it. The artifact here has no user axis at all: every row is a (cohort, series) or a series.
/// </para>
/// </summary>
public record ReaderCohortOptions(string DatabasePath, string StagingDirectory);
