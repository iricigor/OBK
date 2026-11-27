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
        "{0} new message", "{0} new messages", "{0} new messages", "{0} · {1}", "{0} · {1}: {2}", "+{0} more", "+{0} more", "+{0} more",
        "Not checked: {0} (sign-in needed)", "Not checked: {0} (check failed)", "No new messages",
        "Messages were not checked", "Checked {0} at {1}.", "{0} and {1}");

    private static Message Make(string id, int hour = 9, string? sender = null) => new()
    {
        Id = id, Title = "Subject " + id, Text = "", Attachments = [],
        SentDate = new DateTimeOffset(2026, 11, 25, hour, 0, 0, TimeSpan.FromHours(1)).ToString("O"),
        Sender = sender is null ? default : System.Text.Json.JsonSerializer.SerializeToElement(new { Name = sender })
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

    private static IEnumerable<string> Ids(IEnumerable<CheckArrival> arrivals) => arrivals.Select(arrival => arrival.Message.Id);

    private static CheckArrival Arrival(string id, int hour = 9, string folder = "received") => new(Make(id, hour), folder);

    private static ChildCheck Check(string child, CheckOutcome outcome, CheckArrival[] arrivals) =>
        new(new Login("https://school.example", child, ChildName: child), child, outcome, arrivals);

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
            Check("Anna", CheckOutcome.Checked, [Arrival("a1", hour: 8), Arrival("a2", hour: 11)]),
            Check("Tomáš", CheckOutcome.Checked, [Arrival("t1", hour: 10)]),
            Check("Eliška", CheckOutcome.SignInNeeded, []),
            Check("Petr", CheckOutcome.Failed, [])
        ], reportEmpty: false, "16:05", English))!;
        Assert.False(notification.IsStatus);
        Assert.Equal("3 new messages", notification.Title);
        Assert.Equal("3 new messages", notification.PublicTitle);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.FromHours(1)), notification.When);
        Assert.Equal(["Anna · Subject a2", "Tomáš · Subject t1", "Anna · Subject a1",
            "Not checked: Eliška (sign-in needed)", "Not checked: Petr (check failed)"], notification.Lines.Select(line => line.Text));
        Assert.Equal(["Anna", "Tomáš", "Anna", null, null], notification.Lines.Select(line => line.Child));
    }

    [Fact]
    public void LongSummariesEndWithTheNumberOfFurtherMessages()
    {
        var arrivals = Enumerable.Range(1, 7).Select(hour => Arrival("m" + hour, hour)).ToArray();
        var notification = InEnglish(() =>
            ScheduledCheckReport.Compose([Check("Anna", CheckOutcome.Checked, arrivals)], false, "16:05", English))!;
        Assert.Equal(ScheduledCheckReport.MaxArrivalLines, notification.Lines.Count);
        Assert.Equal("Anna · Subject m7", notification.Lines[0].Text);
        Assert.Equal("+3 more", notification.Lines[^1].Text);
    }

    [Fact]
    public void ChecksWithoutArrivalsPostAStatusOnlyWhenRequested()
    {
        List<ChildCheck> quiet =
        [
            Check("Anna", CheckOutcome.Checked, []), Check("Tomáš", CheckOutcome.Checked, []),
            Check("Petr", CheckOutcome.Checked, []), Check("Eliška", CheckOutcome.SignInNeeded, [])
        ];
        Assert.Null(ScheduledCheckReport.Compose(quiet, reportEmpty: false, "16:05", English));
        var status = InEnglish(() => ScheduledCheckReport.Compose(quiet, reportEmpty: true, "16:05", English))!;
        Assert.True(status.IsStatus);
        Assert.Equal("No new messages", status.Title);
        Assert.Equal(["Checked Anna, Tomáš and Petr at 16:05.", "Not checked: Eliška (sign-in needed)"],
            status.Lines.Select(line => line.Text));
        Assert.Empty(status.Destination.AccountIds);
        Assert.Equal("received", status.Destination.Folder);
        var failed = InEnglish(() => ScheduledCheckReport.Compose([Check("Anna", CheckOutcome.Failed, [])], true, "16:05", English))!;
        Assert.Equal("Messages were not checked", failed.Title);
        Assert.Empty(failed.Destination.AccountIds);
    }

    [Fact]
    public void SingleArrivalShowsChildAndSenderAboveTheSubjectButNotOnTheLockScreen()
    {
        var notification = InEnglish(() => ScheduledCheckReport.Compose(
        [
            Check("Marko", CheckOutcome.Checked, [new CheckArrival(Make("m", sender: "Veronika  Lachnitová"), "received")]),
            Check("Petr", CheckOutcome.Failed, [])
        ], false, "16:05", English))!;
        Assert.Equal("Marko · Veronika Lachnitová", notification.Title);
        Assert.Equal("1 new message", notification.PublicTitle);
        Assert.Equal(["Subject m", "Not checked: Petr (check failed)"], notification.Lines.Select(line => line.Text));
        var anonymous = InEnglish(() => ScheduledCheckReport.Compose(
            [Check("Marko", CheckOutcome.Checked, [Arrival("m")])], false, "16:05", English))!;
        Assert.Equal("Marko", anonymous.Title);
    }

    [Fact]
    public void SeveralArrivalsListSendersWhenSupplied()
    {
        var notification = InEnglish(() => ScheduledCheckReport.Compose(
            [Check("Anna", CheckOutcome.Checked, [new CheckArrival(Make("a", 10, "Eva Novak"), "received"), Arrival("b", 9)])],
            false, "16:05", English))!;
        Assert.Equal(["Anna · Eva Novak: Subject a", "Anna · Subject b"], notification.Lines.Select(line => line.Text));
    }

    [Fact]
    public void SummaryTitleUsesTheLanguagesPluralForms()
    {
        var czech = English with { NewOne = "{0} nová zpráva", NewFew = "{0} nové zprávy", NewMany = "{0} nových zpráv" };
        string Title(int count) => ScheduledCheckReport.Compose(
            [Check("Anna", CheckOutcome.Checked, Enumerable.Range(1, count).Select(hour => Arrival("m" + hour, hour)).ToArray())],
            false, "16:05", czech)!.PublicTitle;
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

    [Fact]
    public void DestinationUsesOnlyChildrenWithArrivalsEvenWhenOtherChecksFail()
    {
        var anna = Check("Anna", CheckOutcome.Checked, [Arrival("a", folder: "noticeboard")]);
        var failed = Check("Petr", CheckOutcome.Failed, []);
        var quiet = Check("Eva", CheckOutcome.Checked, []);
        var notification = InEnglish(() => ScheduledCheckReport.Compose([anna, failed, quiet], false, "16:05", English))!;
        Assert.Equal([NotificationDestination.AccountId(anna.Account)], notification.Destination.AccountIds);
        Assert.Equal("noticeboard", notification.Destination.Folder);
        Assert.Single(notification.Destination.ResolveAccounts([anna.Account, failed.Account, quiet.Account]));
    }

    [Fact]
    public void InboxWinsOverNewerNoticeboardArrivalsEvenOutsideDisplayedLines()
    {
        var anna = Check("Anna", CheckOutcome.Checked,
            Enumerable.Range(10, 7).Select(hour => Arrival("n" + hour, hour, "noticeboard")).ToArray());
        var tomas = Check("Tomas", CheckOutcome.Checked, [Arrival("old-inbox", hour: 8)]);
        var notification = InEnglish(() => ScheduledCheckReport.Compose([anna, tomas], false, "16:05", English))!;
        Assert.Equal("received", notification.Destination.Folder);
        Assert.Equal([NotificationDestination.AccountId(anna.Account), NotificationDestination.AccountId(tomas.Account)],
            notification.Destination.AccountIds);
        Assert.DoesNotContain(notification.Lines, line => line.Text.Contains("old-inbox"));
    }

    [Fact]
    public void ArrivalFolderSurvivesDeduplicationAndOpenedMessageFiltering()
    {
        var arrivals = ScheduledCheckReport.Arrivals(Archive([]),
            Archive(["shared", "read"], noticeboard: ["shared", "notice"]), new HashSet<string> { "read" });
        Assert.Equal(["shared", "notice"], Ids(arrivals));
        Assert.Equal(["received", "noticeboard"], arrivals.Select(arrival => arrival.Folder));
    }

    [Fact]
    public void DuplicateChildNamesDoNotMergeDifferentAccounts()
    {
        var first = Check("Anna", CheckOutcome.Checked, [Arrival("a")]);
        var second = first with { Account = first.Account with { Url = "https://other.example" } };
        var notification = InEnglish(() => ScheduledCheckReport.Compose([first, second], false, "16:05", English))!;
        Assert.Equal(2, notification.Destination.AccountIds.Count);
        Assert.Equal([first.Account, second.Account], notification.Destination.ResolveAccounts([first.Account, second.Account]));
    }

    [Fact]
    public void DestinationPayloadSurvivesRenamingAndNeverIncludesCredentials()
    {
        var login = new Login("https://school.example", "parent", "not-a-real-password", "Anna");
        var other = login with { Username = "other", ChildName = "Anna" };
        var id = NotificationDestination.AccountId(login);
        var destination = NotificationDestination.FromPayload([id, id], "noticeboard");
        var renamed = login with { ChildName = "Renamed child", Password = null };
        Assert.Single(destination.AccountIds);
        Assert.Equal(id, NotificationDestination.AccountId(renamed));
        Assert.DoesNotContain(login.Username, id);
        Assert.DoesNotContain(login.Password!, id);
        Assert.Equal([renamed], destination.ResolveAccounts([other, renamed]));
        Assert.Empty(destination.ResolveAccounts([other]));
        Assert.Equal([other, renamed], NotificationDestination.FromPayload(null, null).ResolveAccounts([other, renamed]));
    }

    [Theory]
    [InlineData(null, "received")]
    [InlineData(new string[0], "noticeboard")]
    [InlineData(new string[0], "sent")]
    [InlineData(new string[] { "invalid-account" }, "received")]
    public void MalformedDestinationsAreRejectedInsteadOfBroadeningScope(string[]? ids, string folder) =>
        Assert.Throws<InvalidDataException>(() => NotificationDestination.FromPayload(ids, folder));
}
