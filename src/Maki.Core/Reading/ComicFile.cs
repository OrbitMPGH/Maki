namespace Maki.Core.Reading;

/// <summary>
/// Which files the reader can open. A PDF is read in place: nothing converts it to a CBZ, and
/// <see cref="CbzReader"/> renders its pages on demand.
/// </summary>
public static class ComicFile
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".pdf"
    };

    public static bool IsComic(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsCbz(string path) =>
        Path.GetExtension(path).Equals(".cbz", StringComparison.OrdinalIgnoreCase);
}
