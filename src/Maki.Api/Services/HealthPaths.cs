using Maki.Core.Paths;

namespace Maki.Api.Services;

public static class HealthPaths
{
    public static string Resolve(string root, string relative)
    {
        var path = LibraryPaths.Resolve(root, relative) ?? throw new InvalidOperationException("Path is outside the library root");
        var fullRoot = Path.GetFullPath(root);
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic links and junctions are excluded from health operations");
            if (string.Equals(current, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        return path;
    }

    public static IEnumerable<string> Archives(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                if (Path.GetExtension(file).Equals(".cbz", StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (!Path.GetFileName(child).StartsWith('.') && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(child);
        }
    }
}
