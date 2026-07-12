using System.Globalization;
using Mangarr.Core.Entities;

namespace Mangarr.Core.Tests;

public class ChapterMonitoringTests
{
    private static decimal? Num(string? number) =>
        number is null ? null : decimal.Parse(number, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("10.5", true)]
    [InlineData("1.1", true)]
    [InlineData("10", false)]
    [InlineData("10.0", false)]
    [InlineData(null, false)] // one-shots are not specials
    public void IsSpecial(string? number, bool expected) =>
        Assert.Equal(expected, Chapter.IsSpecial(Num(number)));

    [Theory]
    [InlineData(NewChapterMonitorMode.All, "10.5", true)]
    [InlineData(NewChapterMonitorMode.All, "10", true)]
    [InlineData(NewChapterMonitorMode.MainOnly, "10", true)]
    [InlineData(NewChapterMonitorMode.MainOnly, "10.5", false)]
    [InlineData(NewChapterMonitorMode.MainOnly, null, true)] // one-shots count as main
    [InlineData(NewChapterMonitorMode.None, "10", false)]
    [InlineData(NewChapterMonitorMode.None, null, false)]
    public void MonitoredUnder(NewChapterMonitorMode mode, string? number, bool expected) =>
        Assert.Equal(expected, Chapter.MonitoredUnder(mode, Num(number)));
}
