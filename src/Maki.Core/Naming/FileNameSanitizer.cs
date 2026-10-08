namespace Maki.Core.Naming;

public static class FileNameSanitizer
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

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
        result = new string(result.Where(c => !char.IsControl(c)).ToArray());
        result = result.Trim().Trim('.');

        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}
