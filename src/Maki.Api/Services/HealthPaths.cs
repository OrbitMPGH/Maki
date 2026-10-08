using Maki.Core.Paths;
using Maki.Core.Reading;

namespace Maki.Api.Services;

public static class HealthPaths
{
    public static string Resolve(string root, string relative)
    {
        var path = LibraryPaths.Resolve(root, relative) ?? throw new InvalidOperationException("Path is outside the library root");
        if (LibraryPaths.TraversesLink(root, path))
            throw new InvalidOperationException("Symbolic links and junctions are excluded from health operations");
        return path;
    }

    public static IEnumerable<string> Archives(string root) =>
        LibraryPaths.EnumerateFilesNoLinks(root, child => !Path.GetFileName(child).StartsWith('.'))
            .Where(ComicFile.IsComic);
}
