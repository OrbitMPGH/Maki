using System.Text;
using Maki.Metadata.Catalogue;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata.Tests;

public class CatalogueTextTests
{
    /// <summary>
    /// The one test in this file that is load-bearing rather than routine.
    ///
    /// <para>
    /// <see cref="FuzzyTermIndex"/> reads its vocabulary out of an FTS5 index built with
    /// <c>unicode61 remove_diacritics 2</c> and then compares those terms against tokens produced by
    /// <see cref="CatalogueText.Tokenize"/>. If the two ever fold differently, every query token
    /// carrying a diacritic reads as one edit away from the term it should have matched exactly, the
    /// budget is spent repairing a spelling that was already right, and nothing fails loudly. So the
    /// tokenizer is asked directly rather than trusted.
    /// </para>
    /// </summary>
    [Fact]
    public void Normalize_folds_text_the_same_way_the_fts5_tokenizer_does()
    {
        const string fixtureText =
            "Nausicaä of the Valley! Kaguya-sama: Love IS War 2 — don't ﬁght, 進撃の巨人 Ω café";

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE VIRTUAL TABLE t USING fts5(body, tokenize='unicode61 remove_diacritics 2')";
            create.ExecuteNonQuery();
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (body) VALUES ($body)";
            insert.Parameters.AddWithValue("$body", fixtureText);
            insert.ExecuteNonQuery();
        }

        using (var vocab = conn.CreateCommand())
        {
            vocab.CommandText = "CREATE VIRTUAL TABLE v USING fts5vocab(t, 'row')";
            vocab.ExecuteNonQuery();
        }

        var fromSqlite = new HashSet<string>(StringComparer.Ordinal);
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT term FROM v";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                fromSqlite.Add(reader.GetString(0));
            }
        }

        var fromUs = CatalogueText.Tokenize(fixtureText).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(fromSqlite);
        Assert.Equal(fromSqlite.OrderBy(t => t, StringComparer.Ordinal), fromUs.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Junji Ito", "ito junji")]
    [InlineData("ITO, Junji", "ito junji")]
    [InlineData("  junji   ito  ", "ito junji")]
    [InlineData("Naoki Urasawa", "naoki urasawa")]
    public void TokenSortKey_ignores_word_order_case_and_punctuation(string input, string expected) =>
        Assert.Equal(expected, CatalogueText.TokenSortKey(input));

    [Theory]
    [InlineData("Junji Itou", "Junji Ito")]
    [InlineData("Katsuhiro Ootomo", "Katsuhiro Otomo")]
    [InlineData("Kentarou Miura", "Kentaro Miura")]
    public void RomanizationKey_collapses_long_vowel_spellings(string a, string b) =>
        Assert.Equal(CatalogueText.RomanizationKey(a), CatalogueText.RomanizationKey(b));

    [Fact]
    public void RomanizationKey_keeps_doubled_consonants_apart()
    {
        // "Ippo" is not "Ipo": a doubled consonant is phonemic, unlike a doubled vowel.
        Assert.NotEqual(CatalogueText.RomanizationKey("Hajime no Ippo"), CatalogueText.RomanizationKey("Hajime no Ipo"));
    }

    [Theory]
    [InlineData("berserk", true)]
    [InlineData("jujutsu2", true)]
    [InlineData("進撃", false)]
    [InlineData("しんげき", false)]
    [InlineData("진격", false)]
    [InlineData("берсерк", false)]
    [InlineData("", false)]
    public void IsExpandable_admits_only_ascii_words(string token, bool expected) =>
        Assert.Equal(expected, CatalogueText.IsExpandable(token));

    [Theory]
    [InlineData("berserk", "berserk", 0)]
    [InlineData("berserk", "berserck", 1)]
    [InlineData("berserk", "bersrek", 1)] // transposition costs one, not two
    [InlineData("berserk", "bersek", 1)]
    [InlineData("berserk", "beserk", 1)]
    [InlineData("kaisen", "kaisan", 1)]
    [InlineData("abc", "xyz", 3)]
    public void BoundedDistance_matches_the_expected_edit_count(string a, string b, int expected) =>
        Assert.Equal(expected, Distance(a, b, 4));

    [Fact]
    public void BoundedDistance_reports_over_budget_rather_than_the_true_distance()
    {
        // Three edits apart, asked for at most one: the answer is "more than one", not "three".
        Assert.Equal(2, Distance("chainsaw", "chainsawman", 1));
        Assert.Equal(3, Distance("chainsaw", "chainsawman", 2));
        Assert.Equal(3, Distance("chainsaw", "chainsawman", 3));
    }

    [Fact]
    public void BoundedDistance_agrees_with_a_reference_implementation()
    {
        var random = new Random(20260823);
        const string alphabet = "abcdefg";
        for (var i = 0; i < 500; i++)
        {
            var a = RandomWord(random, alphabet);
            var b = RandomWord(random, alphabet);
            var expected = Reference(a, b);
            var actual = Distance(a, b, 8);
            Assert.Equal(Math.Min(expected, 9), Math.Min(actual, 9));
        }

        static string RandomWord(Random random, string alphabet)
        {
            var length = random.Next(1, 9);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            return new string(chars);
        }
    }

    private static int Distance(string a, string b, int max) =>
        CatalogueText.BoundedDistance<byte>(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b), max);

    /// <summary>Textbook unbounded optimal string alignment, to check the banded, aborting one against.</summary>
    private static int Reference(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
