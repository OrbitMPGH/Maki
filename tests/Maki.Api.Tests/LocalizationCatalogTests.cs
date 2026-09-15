using System.Text;
using System.Text.RegularExpressions;
using Maki.Core.Localization;

namespace Maki.Api.Tests;

/// <summary>
/// Keeps the server message catalogues honest against the C# that names their keys.
/// <para>
/// This test is the reason call sites can write a literal key instead of declaring ~150 string
/// constants. Without it, a key renamed in C# and not in the PO file, or a placeholder added to the
/// English message and not to the Swedish one, is invisible until somebody hits that exact error
/// path in that exact language. With it, both are build failures.
/// </para>
/// </summary>
public class LocalizationCatalogTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string LocalesDir = Path.Combine(RepoRoot, "locales");

    [Fact]
    public void EveryLanguageHasACatalogue()
    {
        foreach (var locale in SupportedLanguages.All)
        {
            Assert.True(
                File.Exists(CatalogPath(locale)),
                $"{locale} is on SupportedLanguages.All but locales/{locale}/server.po does not exist. " +
                "The app would fall back to English for it with only a log line to say so.");
        }
    }

    [Fact]
    public void EveryKeyUsedInCodeExistsInEnglish()
    {
        var english = Keys("en");
        var used = KeysUsedInCode();

        Assert.NotEmpty(used);
        var missing = used.Where(k => !english.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0,
            "These keys are named in C# but absent from locales/en/server.po, so they would render " +
            "as the raw key: " + string.Join(", ", missing));
    }

    [Fact]
    public void EnglishHasNoOrphans()
    {
        var used = KeysUsedInCode();
        var orphans = Keys("en").Where(k => !used.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(orphans.Count == 0,
            "These keys are in locales/en/server.po but nothing names them any more. Delete them, or " +
            "thirteen translators will be asked to translate dead strings: " + string.Join(", ", orphans));
    }

    [Fact]
    public void EveryLanguageDefinesTheSameKeys()
    {
        var english = Keys("en");
        foreach (var locale in SupportedLanguages.All.Where(l => l != "en"))
        {
            var theirs = Keys(locale);

            var extra = theirs.Except(english).OrderBy(k => k).ToList();
            Assert.True(extra.Count == 0,
                $"locales/{locale}/server.po defines keys English does not: {string.Join(", ", extra)}");

            var absent = english.Except(theirs).OrderBy(k => k).ToList();
            Assert.True(absent.Count == 0,
                $"locales/{locale}/server.po is missing keys: {string.Join(", ", absent)}. " +
                "An entry may be present and empty, which reads as untranslated, but it must be present.");
        }
    }

    [Fact]
    public void TranslationsKeepTheirPlaceholders()
    {
        var english = Entries("en");

        foreach (var locale in SupportedLanguages.All.Where(l => l != "en"))
        {
            foreach (var (key, translation) in Entries(locale))
            {
                // Empty means untranslated, which is the normal state for most of these and falls
                // back to English at runtime. Only a translation that exists has to be right.
                if (string.IsNullOrWhiteSpace(translation)) continue;
                if (!english.TryGetValue(key, out var source)) continue;

                Assert.True(
                    Placeholders(source).SetEquals(Placeholders(translation)),
                    $"locales/{locale}/server.po key '{key}' does not use the same placeholders as " +
                    $"English. English has [{string.Join(", ", Placeholders(source).Order())}], " +
                    $"the translation has [{string.Join(", ", Placeholders(translation).Order())}]. " +
                    "A dropped placeholder loses information; an invented one throws at format time.");
            }
        }
    }

    private static string CatalogPath(string locale) => Path.Combine(LocalesDir, locale, "server.po");

    private static HashSet<string> Keys(string locale) =>
        Entries(locale).Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Key to translation, for the singular form. Deliberately a small hand-rolled reader rather
    /// than a PO library: the test project has no reason to take that dependency, and a parser bug
    /// here would be a test that passes for the wrong reason.
    /// </summary>
    private static Dictionary<string, string> Entries(string locale)
    {
        var path = CatalogPath(locale);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;

        string? key = null;
        var value = new StringBuilder();
        var inValue = false;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            // Obsolete entries. The extractor keeps these so a translation survives a reworded
            // source, but they are not part of the live catalogue.
            if (line.StartsWith("#~", StringComparison.Ordinal)) continue;

            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            {
                Flush(result, key, value, inValue);
                key = Unquote(line[6..]);
                value.Clear();
                inValue = false;
            }
            else if (line.StartsWith("msgstr ", StringComparison.Ordinal))
            {
                value.Clear();
                value.Append(Unquote(line[7..]));
                inValue = true;
            }
            else if (inValue && line.StartsWith("\"", StringComparison.Ordinal))
            {
                // A message folded across several quoted lines.
                value.Append(Unquote(line));
            }
            else if (line.Length == 0)
            {
                Flush(result, key, value, inValue);
                key = null;
                inValue = false;
            }
        }
        Flush(result, key, value, inValue);

        // The header is an entry with an empty key.
        result.Remove(string.Empty);
        return result;
    }

    private static void Flush(
        Dictionary<string, string> into, string? key, StringBuilder value, bool inValue)
    {
        if (key is not null && inValue) into[key] = value.ToString();
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    /// <summary>
    /// ICU placeholder names. Only the simple <c>{name}</c> form, which is what these messages use;
    /// the opening name of a plural block reads the same way.
    /// </summary>
    private static HashSet<string> Placeholders(string message) =>
        Regex.Matches(message, @"\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*[,}]")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every key named from C#. Matches the two shapes call sites use: the ApiResults helpers, and
    /// ILocalizer directly.
    /// </summary>
    private static HashSet<string> KeysUsedInCode()
    {
        var pattern = new Regex(
            @"(?:Fail|NotFoundMessage|Conflict|Get|GetFor)\s*\([^)]*?""((?:error|inbox|achievement|queue|health|scrobble|opds)\.[A-Za-z0-9_.]+)""",
            RegexOptions.Compiled);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;

            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
            {
                keys.Add(m.Groups[1].Value);
            }
        }
        return keys;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Maki.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
