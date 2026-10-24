using System.Security.Cryptography;
using System.Text;

namespace Maki.Core.Reading;

/// <summary>
/// The document ids KOReader's progress sync plugin sends, computed the way KOReader computes them.
/// </summary>
public static class KoreaderDigest
{
    private const int Step = 1024;
    private const int SampleSize = 1024;

    /// <summary>
    /// KOReader's "binary" matching (<c>util.partialMD5</c>): MD5 over up to 1 KiB read at offsets 0,
    /// 1 KiB, 4 KiB, 16 KiB and so on, quadrupling up to 1 GiB, stopping at the first offset past the
    /// end. The first offset is <c>lshift(1024, -2)</c>, which LuaJIT evaluates to 0.
    /// </summary>
    public static string PartialMd5(Stream stream)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[SampleSize];
        var length = stream.Length;
        for (var i = -1; i <= 10; i++)
        {
            long offset = i < 0 ? 0 : (long)Step << (2 * i);
            if (offset >= length)
            {
                break;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < SampleSize)
            {
                var n = stream.Read(buffer, read, SampleSize - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            md5.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    public static string PartialMd5(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.RandomAccess);
        return PartialMd5(stream);
    }

    /// <summary>KOReader's "filename" matching: MD5 of the file's base name, extension included.</summary>
    public static string FileName(string fileName) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(fileName)));

    /// <summary>
    /// The names a file Maki served as <paramref name="names"/> may carry on a device. KOReader's
    /// <c>util.replaceAllInvalidChars</c> runs on FAT storage and on Android, so each name also
    /// counts with <c>\/:*?"&lt;&gt;|</c> replaced and trailing dots and spaces trimmed from the stem.
    /// </summary>
    public static IReadOnlyList<string> FileNameVariants(IEnumerable<string> names)
    {
        var variants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            variants.Add(name);
            var extension = Path.GetExtension(name);
            var stem = name[..^extension.Length];
            var safeStem = string.Concat(stem.Select(c => "\\/:*?\"<>|".Contains(c) ? '_' : c)).TrimEnd('.', ' ', '\t');
            variants.Add(safeStem + extension);
        }

        return [.. variants];
    }
}
