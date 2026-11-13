using System.IO.Compression;
using Maki.Api.Configuration;
using Maki.Core.Images;

namespace Maki.Api.Services;

/// <summary>
/// Each chapter's two edge strips on disk (<c>cache/credits/{key[..2]}/{key}.s{StripVersion}.edges</c>,
/// deflated, a few tens of KB), keyed by the slice's edge key, so a chapter is decoded once however
/// many neighbours compare against it, and once across restarts. Losing the folder loses no answers,
/// only the decodes. <c>HousekeepingJob</c> drops strips no stored answer names.
/// </summary>
public sealed class CreditStrips(AppPaths paths, ILogger<CreditStrips> logger)
{
    public string Root => RootOf(paths);

    public static string RootOf(AppPaths paths) => Path.Combine(paths.CacheDir, "credits");

    /// <summary>How long a strip no stored answer names is kept: a reference whose own chapter is still waiting for the scan.</summary>
    public static readonly TimeSpan UnnamedLifetime = TimeSpan.FromDays(7);

    public CreditDetector.Edges? Read(string edgeKey)
    {
        var path = PathOf(edgeKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var inflate = new DeflateStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(inflate);
            return CreditDetector.Edges.Read(reader);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogDebug(e, "Could not read the credit strips {Path}", path);
            TryDelete(path);
            return null;
        }
    }

    public void Write(string edgeKey, CreditDetector.Edges edges)
    {
        var path = PathOf(edgeKey);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = File.Create(temp))
            using (var deflate = new DeflateStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(deflate))
            {
                edges.Write(writer);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(e, "Could not write the credit strips {Path}", path);
            TryDelete(temp);
        }
    }

    /// <summary>The edge key a strip file holds, or null for a file that is not one of the current version.</summary>
    public static string? KeyOf(string fileName)
    {
        var suffix = $".s{CreditDetector.StripVersion}.edges";
        return fileName.EndsWith(suffix, StringComparison.Ordinal) ? fileName[..^suffix.Length] : null;
    }

    private string PathOf(string edgeKey) =>
        Path.Combine(Root, edgeKey[..Math.Min(2, edgeKey.Length)], $"{edgeKey}.s{CreditDetector.StripVersion}.edges");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
