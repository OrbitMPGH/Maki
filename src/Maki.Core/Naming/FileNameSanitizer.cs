using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Maki.Core.Naming;

public static class FileNameSanitizer
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// UTF-8 bytes a sanitized name may take. ext4 and btrfs cap a name at 255 bytes, and Maki adds
    /// to it afterwards: a language suffix, the extension, ".partial", a " [mb-id]" or " (2)"
    /// collision suffix, and the id prefix of a trashed file.
    /// </summary>
    public const int MaxBytes = 200;

    private const int TailBytes = 48;

    /// <summary>Makes a string safe to use as a folder or file name on Windows and Linux.</summary>
    public static string Sanitize(string name)
    {
        var result = name;
        foreach (var c in InvalidChars)
        {
            result = result.Replace(c.ToString(), string.Empty);
        }

        // Control chars and trailing dots and spaces are invalid on Windows; a leading dot hides
        // the name on Linux, and Maki's own import and health scans skip dot folders.
        result = TrimEdges(new string(result.Where(c => !char.IsControl(c)).ToArray()));
        if (result.Length == 0)
        {
            return "_";
        }

        return AvoidReserved(Shorten(result));
    }

    private static string TrimEdges(string value)
    {
        string trimmed;
        do
        {
            trimmed = value;
            value = value.Trim().Trim('.');
        }
        while (value.Length != trimmed.Length);

        return value;
    }

    /// <summary>"AUX" and "aux.foo" both name the device on Windows, so the part before the first dot decides.</summary>
    private static string AvoidReserved(string value)
    {
        var dot = value.IndexOf('.');
        var stem = (dot < 0 ? value : value[..dot]).TrimEnd();
        return ReservedNames.Contains(stem) ? stem + "_" + value[stem.Length..] : value;
    }

    /// <summary>
    /// Keeps the start and the end of an over-long name, since the end is where a chapter format puts
    /// the volume and chapter, and joins them with a hash of the whole name so two names that differ
    /// only in the dropped middle stay apart.
    /// </summary>
    private static string Shorten(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxBytes)
        {
            return value;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
        var joint = $" ~{hash} ";
        var elements = TextElements(value);
        var tail = TrimEdges(TakeBytes(elements.AsEnumerable().Reverse(), TailBytes, reverse: true));
        var head = TrimEdges(TakeBytes(elements, MaxBytes - TailBytes - Encoding.UTF8.GetByteCount(joint), reverse: false));
        return TrimEdges(head + joint + tail);
    }

    private static List<string> TextElements(string value)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return elements;
    }

    private static string TakeBytes(IEnumerable<string> elements, int maxBytes, bool reverse)
    {
        var taken = new List<string>();
        var bytes = 0;
        foreach (var element in elements)
        {
            bytes += Encoding.UTF8.GetByteCount(element);
            if (bytes > maxBytes)
            {
                break;
            }

            taken.Add(element);
        }

        if (reverse)
        {
            taken.Reverse();
        }

        return string.Concat(taken);
    }
}
