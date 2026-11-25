using OBK.Core.Localization;

namespace OBK.Core;

/// <summary>A possibly related message link shared by the Windows and Android readers.</summary>
public sealed record RelatedLink(MessageRef Item, bool IsNext, int? Number, string When)
{
    public string Direction => Number is { } number
        ? Localized.Format(IsNext ? SharedStrings.RelatedNextNumbered : SharedStrings.RelatedPreviousNumbered, number)
        : IsNext ? SharedStrings.RelatedNext : SharedStrings.RelatedPrevious;
    public bool FromYou => Item.Folder == "sent";
    public string FullDate => Item.Message.Date.ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture);
}

public static class RelatedLinks
{
    /// <summary>
    /// Chronological links; numbers count outward from the displayed message when a side has several.
    /// Dates are relative to <paramref name="displayed"/> (see <see cref="RelativeDay"/>).
    /// </summary>
    public static IReadOnlyList<RelatedLink> Build(RelatedMessages related, DateTimeOffset displayed, DateTime today)
    {
        RelatedLink Link(RelatedMessage match, bool next, int index, int count) =>
            new(match.Item, next, count > 1 ? index + 1 : null, RelativeDay(match.Item.Message.Date, displayed, today));
        var previous = related.Previous.Select((match, index) => Link(match, false, index, related.Previous.Count)).Reverse();
        var following = related.Responses.Select((match, index) => Link(match, true, index, related.Responses.Count));
        return previous.Concat(following).ToArray();
    }

    /// <summary>
    /// Labels a related message's date by calendar days in local time. A displayed message received today anchors
    /// labels to today (Today with the time, Yesterday, N days ago); an older one anchors them to its own date
    /// (Same day with the time, N days before, N days after).
    /// </summary>
    public static string RelativeDay(DateTimeOffset date, DateTimeOffset displayed, DateTime today)
    {
        var local = date.LocalDateTime;
        var day = local.Date;
        var anchor = displayed.LocalDateTime.Date;
        today = today.Date;
        string Time() => local.ToString(SharedStrings.DateTimeFormat, LanguageSettings.Culture);
        if (anchor >= today)
        {
            var ago = (today - day).Days;
            return ago <= 0 ? Localized.Format(SharedStrings.RelatedTodayAt, Time()) : ago == 1 ? SharedStrings.GroupYesterday :
                Localized.Plural(ago, SharedStrings.RelatedDaysAgoOne, SharedStrings.RelatedDaysAgoFew, SharedStrings.RelatedDaysAgoMany);
        }
        var offset = (day - anchor).Days;
        return offset == 0 ? Localized.Format(SharedStrings.RelatedSameDayAt, Time()) : offset < 0
            ? Localized.Plural(-offset, SharedStrings.RelatedDaysBeforeOne, SharedStrings.RelatedDaysBeforeFew, SharedStrings.RelatedDaysBeforeMany)
            : Localized.Plural(offset, SharedStrings.RelatedDaysAfterOne, SharedStrings.RelatedDaysAfterFew, SharedStrings.RelatedDaysAfterMany);
    }
}
