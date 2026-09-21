using System.Globalization;
using System.Text.RegularExpressions;
using Maki.Core.Parsing;

namespace Maki.Api.Tests;

/// <summary>
/// Guards the one localization hazard in this codebase that a green build will not catch.
/// <para>
/// Chapter numbers are decimals parsed from source strings, and roughly thirty sites parse and
/// format them with <see cref="CultureInfo.InvariantCulture"/> on purpose. Under a German or
/// Turkish ambient culture, a parse that forgot the invariant reads "12.5" as one hundred and
/// twenty five: no exception, no log line, and chapter 12.5 filed as chapter 125 in a user's
/// library. Nothing about that failure looks like a localization bug afterwards.
/// </para>
/// <para>
/// So there are two tests. One drills the parser under hostile cultures, proving the current code
/// is culture-proof. The other scans the source for the constructs that would reintroduce the
/// hazard, because the first test only covers the paths it happens to call and someone adding
/// <c>UseRequestLocalization</c> later would sail past it.
/// </para>
/// </summary>
public class AmbientCultureTests
{
    /// <summary>
    /// Cultures picked for the two ways they break a naive parse. German swaps the decimal
    /// separator for a comma, so "12.5" becomes a group separator and parses as 125. Turkish does
    /// that and also has a dotless i, which breaks case-insensitive matching on ASCII "I".
    /// </summary>
    public static TheoryData<string> HostileCultures() => new() { "de-DE", "tr-TR", "sv-SE", "ru-RU" };

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void ChapterNumbersParseTheSameUnderAnyCulture(string culture)
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            var hostile = new CultureInfo(culture);
            CultureInfo.CurrentCulture = hostile;
            CultureInfo.CurrentUICulture = hostile;

            // The decisive case. 12.5 read as 125 files a special between chapters 124 and 126.
            Assert.Equal(12.5m, ChapterNumberParser.Parse("Chapter 12.5").Number);
            Assert.Equal(12.5m, ChapterNumberParser.Parse("12.5").Number);
            Assert.Equal(1234.5m, ChapterNumberParser.Parse("Ch. 1234.5").Number);

            // Whole numbers have no separator to misread, so a failure here means something worse.
            Assert.Equal(1191m, ChapterNumberParser.Parse("Chapter 1191").Number);

            // Volume and one-shot detection match case-insensitively on ASCII, which is what the
            // Turkish dotless i breaks.
            Assert.Equal(3, ChapterNumberParser.Parse("Vol. 3 Ch. 7", "3").Volume);
            Assert.True(ChapterNumberParser.Parse("One-Shot").IsOneShot);
            Assert.True(ChapterNumberParser.Parse("ONE-SHOT").IsOneShot);

            // Round trip. A number formatted under a comma culture and read back would drift.
            var parsed = ChapterNumberParser.Parse("Chapter 12.5").Number!.Value;
            Assert.Equal("12.5", parsed.ToString("0.###", CultureInfo.InvariantCulture));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = original;
        }
    }

    /// <summary>
    /// The app must never set ambient culture, because setting it changes number parsing process
    /// wide rather than just the language of a message. Localization here goes through
    /// <c>IRequestLocale</c> and an explicit <c>CultureInfo</c> at format time instead.
    /// </summary>
    [Fact]
    public void NothingSetsAmbientCultureOrUsesRequestLocalization()
    {
        // Assigning either culture property, or the thread's, or asking ASP.NET to do it per request.
        var banned = new Regex(
            @"UseRequestLocalization|AddRequestLocalization|RequestLocalizationOptions"
            + @"|(?:CultureInfo\.)?(?:DefaultThread)?Current(?:UI)?Culture\s*=",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // Comments are skipped, and the reason is not pedantry: the places that explain why
                // this rule exists naturally quote the thing it forbids, so the first run of this
                // test was failed by its own documentation. A line that starts with code and ends
                // in a comment is still checked, which is the case that matters.
                var code = lines[i].TrimStart();
                if (code.StartsWith("//") || code.StartsWith("*") || code.StartsWith("/*")) continue;

                if (banned.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Ambient culture must never be set, and UseRequestLocalization must never be called. "
            + "Both change decimal parsing for the whole process, and ~30 sites parse chapter "
            + "numbers and file sizes with InvariantCulture on the assumption that nothing does "
            + "this. Localize through IRequestLocale and pass an explicit CultureInfo when "
            + "formatting.\n  " + string.Join("\n  ", offenders));
    }

    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>Every shipped .cs file. Tests are excluded: this one sets culture deliberately.</summary>
    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Maki.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
