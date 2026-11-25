using OBK.AndroidApp;
using OBK.Core;
using OBK.Core.Localization;
using System.Globalization;
using Xunit;

namespace OBK.Tests;

[Collection("Language")]
public sealed class ScheduledCheckReportTests
{
    private static readonly CheckTexts English = new(
        "{0} new message", "{0} new messages", "{0} new messages", "{0} · {1}", "+{0} more", "+{0} more", "+{0} more",
        "Not checked: {0} (sign-in needed)", "Not checked: {0} (check failed)", "No new messages",
        "Messages were not checked", "Checked {0} at {1}.", "{0} and {1}");

    private static Message Make(string id, int hour = 9) => new()
    {
        Id = id, Title = "Subject " + id, Text = "", Attachments = [],
        SentDate = new DateTimeOffset(2026, 11, 25, hour, 0, 0, TimeSpan.FromHours(1)).ToString("O")
    };

    private static Dictionary<string, List<Message>> Archive(string[] received, string[]? noticeboard = null, string[]? sent = null)
    {
        var folders = Folders.Empty();
        folders["received"].AddRange(received.Select(id => Make(id)));
        folders["noticeboard"].AddRange((noticeboard ?? []).Select(id => Make(id)));
        folders["sent"].AddRange((sent ?? []).Select(id => Make(id)));
        return folders;
    }

    private static T InEnglish<T>(Func<T> action)
    {
        var original = SharedStrings.Culture;
        SharedStrings.Culture = CultureInfo.GetCultureInfo("en");
        try { return action(); }
        finally { SharedStrings.Culture = original; }
    }

    private static IEnumerable<string> Ids(IEnumerable<Message> messages) => messages.Select(message => message.Id);

    [Fact]
    public void ArrivalsAreUnopenedInboxAndNoticeboardMessagesTheCheckAdded()
    {
        var before = Archive(["old"]);
        var after = Archive(["old", "new", "new"], noticeboard: ["notice"], sent: ["reply"]);
        Assert.Equal(["new", "notice"], Ids(ScheduledCheckReport.Arrivals(before, after, new HashSet<string> { "old" })));
    }

    [Fact]
    public void NewSchoolYearAnnouncesUnopenedMessagesButAFirstDownloadDoesNot()
    {
        var after = Archive(["september"], noticeboard: ["notice"]);
        Assert.Equal(["september", "notice"],
            Ids(ScheduledCheckReport.Arrivals(null, after, new HashSet<string> { "last-year" })));
        Assert.Empty(ScheduledCheckReport.Arrivals(null, after, new HashSet<string> { "september", "notice" }));
    }

    [Fact]
    public void SummaryListsNewestArrivalsFirstThenChildrenThatWereNotChecked()
    {
        var notification = InEnglish(() => ScheduledCheckReport.Compose(
        [
            new("Anna", CheckOutcome.Checked, [Make("a1", hour: 8), Make("a2", hour: 11)]),
            new("Tomáš", CheckOutcome.Checked, [Make("t1", hour: 10)]),
            new("Eliška", CheckOutcome.SignInNeeded, []),
            new("Petr", CheckOutcome.Failed, [])
        ], reportEmpty: false, "16:05", English))!;
        Assert.False(notification.IsStatus);
        Assert.Equal("3 new messages", notification.Title);
        Assert.Equal(["Anna · Subject a2", "Tomáš · Subject t1", "Anna · Subject a1",
            "Not checked: Eliška (sign-in needed)", "Not checked: Petr (check failed)"], notification.Lines.Select(line => line.Text));
        Assert.Equal(["Anna", "Tomáš", "Anna", null, null], notification.Lines.Select(line => line.Child));
    }

    [Fact]
    public void LongSummariesEndWithTheNumberOfFurtherMessages()
    {
        var arrivals = Enumerable.Range(1, 7).Select(hour => Make("m" + hour, hour)).ToArray();
        var notification = InEnglish(() =>
            ScheduledCheckReport.Compose([new("Anna", CheckOutcome.Checked, arrivals)], false, "16:05", English))!;
        Assert.Equal(ScheduledCheckReport.MaxArrivalLines, notification.Lines.Count);
        Assert.Equal("Anna · Subject m7", notification.Lines[0].Text);
        Assert.Equal("+3 more", notification.Lines[^1].Text);
    }

    [Fact]
    public void ChecksWithoutArrivalsPostAStatusOnlyWhenRequested()
    {
        List<ChildCheck> quiet =
        [
            new("Anna", CheckOutcome.Checked, []), new("Tomáš", CheckOutcome.Checked, []),
            new("Petr", CheckOutcome.Checked, []), new("Eliška", CheckOutcome.SignInNeeded, [])
        ];
        Assert.Null(ScheduledCheckReport.Compose(quiet, reportEmpty: false, "16:05", English));
        var status = InEnglish(() => ScheduledCheckReport.Compose(quiet, reportEmpty: true, "16:05", English))!;
        Assert.True(status.IsStatus);
        Assert.Equal("No new messages", status.Title);
        Assert.Equal(["Checked Anna, Tomáš and Petr at 16:05.", "Not checked: Eliška (sign-in needed)"],
            status.Lines.Select(line => line.Text));
        var failed = InEnglish(() => ScheduledCheckReport.Compose([new("Anna", CheckOutcome.Failed, [])], true, "16:05", English))!;
        Assert.Equal("Messages were not checked", failed.Title);
    }

    [Fact]
    public void SummaryTitleUsesTheLanguagesPluralForms()
    {
        var czech = English with { NewOne = "{0} nová zpráva", NewFew = "{0} nové zprávy", NewMany = "{0} nových zpráv" };
        string Title(int count) => ScheduledCheckReport.Compose(
            [new("Anna", CheckOutcome.Checked, Enumerable.Range(1, count).Select(hour => Make("m" + hour, hour)).ToArray())],
            false, "16:05", czech)!.Title;
        var original = SharedStrings.Culture;
        SharedStrings.Culture = CultureInfo.GetCultureInfo("cs");
        try
        {
            Assert.Equal("1 nová zpráva", Title(1));
            Assert.Equal("3 nové zprávy", Title(3));
            Assert.Equal("5 nových zpráv", Title(5));
        }
        finally { SharedStrings.Culture = original; }
    }
}
