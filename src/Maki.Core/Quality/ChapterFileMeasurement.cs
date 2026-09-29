namespace Maki.Core.Quality;

/// <summary>
/// What <see cref="ChapterFileMeasurer"/> read off a chapter's pages: how many there are, their
/// typical dimensions, and which image format they are in. An archive measurement is sampled
/// rather than exhaustive, so this describes a large file well enough to compare two candidates,
/// not exactly.
/// </summary>
public record ChapterFileMeasurement(int PageCount, int? MedianWidth, int? MedianHeight, string ImageFormat);
