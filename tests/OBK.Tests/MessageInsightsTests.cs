using OBK.Core;
using System.Text.Json;
using Xunit;

namespace OBK.Tests;

public sealed class MessageInsightsTests
{
    private static Message Make(string id, string title, string date, string? sender = null, string? recipient = null,
        string text = "", string? type = null) => new()
    {
        Id = id, Title = title, Text = text, SentDate = date, Attachments = [],
        Sender = sender is null ? default : JsonSerializer.SerializeToElement(new { Name = sender }),
        RelevantName = recipient,
        Extra = type is null ? null : new() { ["Type"] = JsonSerializer.SerializeToElement(type) }
    };

    [Fact]
    public void SearchMatchesTitleBodySenderAndDateIgnoringCaseAndDiacritics()
    {
        var message = Make("1", "Školní výlet", "2026-11-03T08:15:00+01:00", "Jana Nováková",
            text: "<p>Sraz v <b>osm</b> hodin</p>");
        var text = MessageInsights.SearchText(message, "received");
        Assert.True(MessageInsights.Matches(text, "skolni VYLET"));
        Assert.True(MessageInsights.Matches(text, "sraz osm"));
        Assert.True(MessageInsights.Matches(text, "novakova"));
        Assert.True(MessageInsights.Matches(text, "3.11.2026"));
        Assert.True(MessageInsights.Matches(text, "november 2026"));
        Assert.True(MessageInsights.Matches(text, "листопада 2026"));
        Assert.True(MessageInsights.Matches(text, "2026-11-03"));
        Assert.True(MessageInsights.Matches(text, "  "));
        Assert.False(MessageInsights.Matches(text, "vylet plavani"));
    }

    [Fact]
    public void ExcusesAreIdentifiedOnlyByTheApiType()
    {
        Assert.True(MessageInsights.IsExcuse(Make("1", "Anything", "2026-11-03T08:00:00+01:00", type: "OMLUVENKA")));
        Assert.False(MessageInsights.IsExcuse(Make("2", "omluvenka: Honza", "2026-11-03T08:00:00+01:00", type: "OBECNA")));
        Assert.False(MessageInsights.IsExcuse(Make("3", "omluvenka: Honza", "2026-11-03T08:00:00+01:00")));
    }

    [Fact]
    public void ArrangeSortsAndKeepsGroupsContiguous()
    {
        var items = new[]
        {
            Make("a", "Re: Beta", "2026-11-20T10:00:00+01:00", "Zed"),
            Make("b", "Alpha", "2026-11-21T10:00:00+01:00", "Adam"),
            Make("c", "beta", "2026-10-01T10:00:00+02:00", "Adam")
        };
        var today = new DateTime(2026, 11, 21);
        string Ids(IEnumerable<(Message Item, string Group)> rows) => string.Concat(rows.Select(r => r.Item.Id));
        Assert.Equal("bac", Ids(MessageInsights.Arrange(items, m => m, m => m.SenderName, MessageSort.Date, MessageGrouping.None, today)));
        Assert.Equal("bca", Ids(MessageInsights.Arrange(items, m => m, m => m.SenderName, MessageSort.Sender, MessageGrouping.None, today)));
        Assert.Equal("bac", Ids(MessageInsights.Arrange(items, m => m, m => m.SenderName, MessageSort.Title, MessageGrouping.None, today)));

        var bySender = MessageInsights.Arrange(items, m => m, m => m.SenderName, MessageSort.Date, MessageGrouping.Sender, today);
        Assert.Equal("bca", Ids(bySender));
        Assert.Equal(["Adam", "Adam", "Zed"], bySender.Select(r => r.Group));

        var byTitle = MessageInsights.Arrange(items, m => m, m => m.SenderName, MessageSort.Date, MessageGrouping.Title, today);
        Assert.Equal("bac", Ids(byTitle));
        Assert.Equal(byTitle[1].Group, byTitle[2].Group);

        var byDate = MessageInsights.Arrange(items, m => m, m => m.SenderName, MessageSort.Title, MessageGrouping.Date, today);
        Assert.Equal(["Today", "Yesterday"], byDate.Take(2).Select(r => r.Group));
        Assert.Equal("c", byDate[2].Item.Id);
    }

    [Fact]
    public void SubjectScoreToleratesPrefixesAndDifferentMiddleParts()
    {
        Assert.Equal(1, MessageInsights.SubjectScore("RE: Odp: Hodnocení", "hodnoceni"));
        Assert.True(MessageInsights.SubjectScore("Hodnocení / Evaluation - Matematika 7.A",
            "Re: Hodnocení - Matematika 7.A") >= 0.6);
        Assert.Equal(0, MessageInsights.SubjectScore("Info", "Info o výletu"));
        Assert.Equal(0, MessageInsights.SubjectScore("Třídní schůzky", "Školní výlet"));
    }

    [Fact]
    public void FindRelatedReturnsPossiblePreviousMessagesAndResponsesWithTheSamePerson()
    {
        var original = Make("1", "Hodnocení / Evaluation - Matematika", "2026-11-01T08:00:00+01:00", "Mgr. Jan Novák");
        var reply = Make("2", "Re: Hodnocení - Matematika", "2026-11-02T08:00:00+01:00", "Parent", "Jan Novák");
        var otherPerson = Make("3", "Re: Hodnocení - Matematika", "2026-11-03T08:00:00+01:00", "Parent", "Eva Malá");
        var unrelated = Make("4", "Školní výlet", "2026-11-04T08:00:00+01:00", "Mgr. Jan Novák");
        var all = new[]
        {
            new MessageRef(original, "received"), new MessageRef(reply, "sent"),
            new MessageRef(otherPerson, "sent"), new MessageRef(unrelated, "received")
        };
        var fromOriginal = MessageInsights.FindRelated(all[0], all);
        Assert.Empty(fromOriginal.Previous);
        Assert.Equal("2", Assert.Single(fromOriginal.Responses).Item.Message.Id);
        var fromReply = MessageInsights.FindRelated(all[1], all);
        Assert.Equal("1", Assert.Single(fromReply.Previous).Item.Message.Id);

        var excuse = new MessageRef(Make("5", "omluvenka: Honza", "2026-11-05T08:00:00+01:00", "Parent", "Jan Novák", type: "OMLUVENKA"), "sent");
        var nextExcuse = new MessageRef(Make("6", "omluvenka: Honza", "2026-11-06T08:00:00+01:00", "Parent", "Jan Novák", type: "OMLUVENKA"), "sent");
        Assert.Empty(MessageInsights.FindRelated(excuse, [excuse, nextExcuse]).Responses);
    }

    [Fact]
    public void RepeatedSubjectsLinkOnlyAdjacentOrSimilarMessages()
    {
        MessageRef Excuse(string id, int day) => new(Make(id, "omluvenka: Irić Marko, 1.T", $"2026-10-{day:00}T08:00:00+02:00",
            "Parent", "Veronika Lachnitová, M.A.", $"<p>Omlouvám syna dne {day}. 10.</p>", "OMLUVENKA"), "sent");
        MessageRef Reply(string id, int day, string text) => new(Make(id, "Re: omluvenka: Irić Marko, 1.T", $"2026-10-{day:00}T12:00:00+02:00",
            "Veronika Lachnitová, M.A.", text: text), "received");
        var e1 = Excuse("e1", 1);
        var r1 = Reply("r1", 2, "<p>Děkuji, beru na vědomí.</p>");
        var e2 = Excuse("e2", 5);
        var r2 = Reply("r2", 6, "<p>Děkuji, omluvenka přijata.</p>");
        var e3 = Excuse("e3", 9);
        var all = new[] { e1, r1, e2, r2, e3 };

        var related = MessageInsights.FindRelated(e2, all);
        Assert.Equal("r1", Assert.Single(related.Previous).Item.Message.Id);
        Assert.Equal("r2", Assert.Single(related.Responses).Item.Message.Id);
        var fromReply = MessageInsights.FindRelated(r1, all);
        Assert.Equal("e1", Assert.Single(fromReply.Previous).Item.Message.Id);
        Assert.Equal("e2", Assert.Single(fromReply.Responses).Item.Message.Id);
    }

    [Fact]
    public void LinksBecomeClickablePartsWithoutDuplicatedOrRelativeDestinations()
    {
        var parts = MessageRenderer.Render(
            "<p>See <a href=\"https://school.example/a\">the page</a>, <a href=\"www.example.cz\">www.example.cz</a>, " +
            "<a href=\"javascript:alert(1)\">bad</a>, <a href=\"/relative\">relative</a> and https://plain.example/x.</p>");
        var text = string.Concat(parts.Select(p => p.Text));
        Assert.Equal("See the page, www.example.cz, bad, relative and https://plain.example/x.\n", text);
        Assert.Equal("https://school.example/a", parts.Single(p => p.Text == "the page").Link);
        Assert.Equal("https://www.example.cz/", parts.Single(p => p.Text == "www.example.cz").Link);
        Assert.Equal("https://plain.example/x", parts.Single(p => p.Text == "https://plain.example/x").Link);
        Assert.Null(parts.Single(p => p.Text.Contains("bad")).Link);
        Assert.DoesNotContain(parts, p => p.Link?.Contains("relative") == true);
        Assert.Equal("mailto:office@school.example", MessageRenderer.NormalizeLink(" mailto:office@school.example "));
        Assert.Null(MessageRenderer.NormalizeLink("file:///C:/secret.txt"));
    }
}
