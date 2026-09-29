using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class NaturalFileNameComparerTests
{
    private static List<string> Sort(params string[] names) =>
        names.OrderBy(n => n, NaturalFileNameComparer.Instance).ToList();

    [Fact]
    public void Unpadded_numbers_sort_numerically_not_lexically()
    {
        Assert.Equal(["1.jpg", "2.jpg", "10.jpg"], Sort("10.jpg", "1.jpg", "2.jpg"));
    }

    [Fact]
    public void Mixed_prefixes_still_compare_text_before_numbers()
    {
        Assert.Equal(["cover.jpg", "img1.jpg", "img2.jpg", "img10.jpg", "page1.jpg"],
            Sort("page1.jpg", "img10.jpg", "img2.jpg", "cover.jpg", "img1.jpg"));
    }

    [Fact]
    public void Multiple_digit_runs_each_compare_numerically()
    {
        Assert.Equal(
            ["x - c049 - p002.jpg", "x - c049 - p113.jpg", "x - c050 - p001.jpg"],
            Sort("x - c050 - p001.jpg", "x - c049 - p113.jpg", "x - c049 - p002.jpg"));
    }

    [Fact]
    public void Equal_numeric_value_with_different_padding_is_ordered_deterministically()
    {
        // "1" and "01" name the same page number; the comparer still needs one fixed answer
        // (rather than depending on input order) so repeated sorts never disagree.
        var first = Sort("01.jpg", "1.jpg");
        var second = Sort("1.jpg", "01.jpg");
        Assert.Equal(first, second);
        Assert.Equal(["1.jpg", "01.jpg"], first);
    }

    [Fact]
    public void Comparer_is_a_strict_total_order_equal_only_when_identical()
    {
        Assert.Equal(0, NaturalFileNameComparer.Instance.Compare("1.jpg", "1.jpg"));
        Assert.NotEqual(0, NaturalFileNameComparer.Instance.Compare("1.jpg", "01.jpg"));
        Assert.True(NaturalFileNameComparer.Instance.Equals("1.jpg", "1.jpg"));
        Assert.False(NaturalFileNameComparer.Instance.Equals("1.jpg", "01.jpg"));
    }

    [Fact]
    public void Case_differences_are_ignored_except_as_a_final_tie_break()
    {
        // Case-insensitive per character, so "B" sorts after "a" the same way "b" does...
        Assert.Equal(["a.jpg", "B.jpg"], Sort("B.jpg", "a.jpg"));
        // ...but two names differing only by case still get one deterministic order, not
        // whichever the sort happened to see first.
        var first = Sort("PAGE.jpg", "page.jpg");
        var second = Sort("page.jpg", "PAGE.jpg");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Null_handling_matches_StringComparer_contract()
    {
        Assert.True(NaturalFileNameComparer.Instance.Compare(null, null) == 0);
        Assert.True(NaturalFileNameComparer.Instance.Compare(null, "a.jpg") < 0);
        Assert.True(NaturalFileNameComparer.Instance.Compare("a.jpg", null) > 0);
    }
}
