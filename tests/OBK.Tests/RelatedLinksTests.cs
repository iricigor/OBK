using OBK.Core;
using OBK.Core.Localization;
using System.Globalization;
using Xunit;

namespace OBK.Tests;

[Collection("Language")]
public sealed class RelatedLinksTests
{
    private static readonly DateTime Today = new(2026, 11, 25);

    private static DateTimeOffset Local(int month, int day, int hour = 9) =>
        new(new DateTime(2026, month, day, hour, 0, 0, DateTimeKind.Local));

    private static string InCulture(string code, Func<string> action)
    {
        var original = SharedStrings.Culture;
        SharedStrings.Culture = CultureInfo.GetCultureInfo(code);
        try { return action(); }
        finally { SharedStrings.Culture = original; }
    }

    [Theory]
    [InlineData(25, 7, "Today 07:00")]
    [InlineData(24, 23, "Yesterday")]
    [InlineData(22, 9, "3 days ago")]
    public void TodaysMessageLabelsRelatedMessagesFromToday(int day, int hour, string expected) =>
        Assert.Equal(expected, InCulture("en", () => RelatedLinks.RelativeDay(Local(11, day, hour), Local(11, 25, 8), Today)));

    [Theory]
    [InlineData(20, "Same day 09:00")]
    [InlineData(19, "1 day before")]
    [InlineData(18, "2 days before")]
    [InlineData(21, "1 day after")]
    [InlineData(25, "5 days after")]
    public void OlderMessageLabelsRelatedMessagesFromItsOwnDate(int day, string expected) =>
        Assert.Equal(expected, InCulture("en", () => RelatedLinks.RelativeDay(Local(11, day), Local(11, 20, 23), Today)));

    [Theory]
    [InlineData("cs", 2, "o 2 dny dříve")]
    [InlineData("cs", 5, "o 5 dní dříve")]
    [InlineData("uk", 21, "на 21 день раніше")]
    [InlineData("uk", 22, "на 22 дні раніше")]
    [InlineData("uk", 12, "на 12 днів раніше")]
    [InlineData("de", 1, "1 Tag vorher")]
    [InlineData("de", 3, "3 Tage vorher")]
    public void DayCountsUseTheLanguagesPluralForms(string code, int days, string expected)
    {
        var displayed = Local(11, 23);
        Assert.Equal(expected, InCulture(code, () => RelatedLinks.RelativeDay(displayed.AddDays(-days), displayed, Today)));
    }

    [Fact]
    public void TodaysMessageUsesTheLanguagesPluralFormsForDaysAgo()
    {
        Assert.Equal("před 3 dny", InCulture("cs", () => RelatedLinks.RelativeDay(Local(11, 22), Local(11, 25), Today)));
        Assert.Equal("5 днів тому", InCulture("uk", () => RelatedLinks.RelativeDay(Local(11, 20), Local(11, 25), Today)));
    }
}
