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

        // Control chars, leading/trailing dots and spaces are invalid on Windows.
        result = new string(result.Where(c => !char.IsControl(c)).ToArray());
        result = result.Trim().TrimEnd('.');

        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}
