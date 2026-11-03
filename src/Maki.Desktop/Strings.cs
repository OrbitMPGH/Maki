using System.Globalization;
using System.Reflection;
using System.Text;

namespace Maki.Desktop;

/// <summary>
/// The tray's text, read from the <c>desktop.*</c> keys of the embedded server catalogues in the
/// Windows display language. An untranslated entry falls back to English, like the server does.
/// Only plain <c>{name}</c> placeholders are used here, so no ICU formatter is needed.
/// </summary>
internal static class Strings
{
    private static readonly Dictionary<string, string> English = Load("en");
    private static readonly Dictionary<string, string> Local = Load(Locale(CultureInfo.CurrentUICulture));

    public static string Get(string key, params (string Name, object Value)[] args)
    {
        var text = Local.TryGetValue(key, out var local) && local.Length > 0 ? local
            : English.TryGetValue(key, out var english) ? english
            : key;
        foreach (var (name, value) in args)
            text = text.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.CurrentCulture));
        return text;
    }

    /// <summary>The catalogue folder for a Windows culture: zh-CN reads zh-Hans, pt-PT reads pt-BR.</summary>
    internal static string Locale(CultureInfo culture)
    {
        var name = culture.Name;
        var available = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("Maki.Locales.", StringComparison.Ordinal))
            .Select(n => n["Maki.Locales.".Length..^".po".Length])
            .ToList();

        return available.FirstOrDefault(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? (culture.TwoLetterISOLanguageName == "zh" ? "zh-Hans" : null)
            ?? available.FirstOrDefault(a => a.Split('-')[0].Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            ?? "en";
    }

    private static Dictionary<string, string> Load(string locale)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Maki.Locales.{locale}.po");
        if (stream is null)
            return entries;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? id = null;
        StringBuilder? value = null;
        void Flush()
        {
            if (id is not null && value is not null && id.StartsWith("desktop.", StringComparison.Ordinal))
                entries[id] = value.ToString();
            id = null;
            value = null;
        }

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            {
                Flush();
                id = Unquote(line["msgid ".Length..]);
            }
            else if (line.StartsWith("msgstr ", StringComparison.Ordinal) && id is not null)
            {
                value = new StringBuilder(Unquote(line["msgstr ".Length..]));
            }
            else if (line.StartsWith('"') && value is not null)
            {
                value.Append(Unquote(line));
            }
        }

        Flush();
        return entries;
    }

    private static string Unquote(string quoted)
    {
        var inner = quoted.Trim();
        if (inner.Length >= 2 && inner[0] == '"' && inner[^1] == '"')
            inner = inner[1..^1];
        return inner.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
    }
}
