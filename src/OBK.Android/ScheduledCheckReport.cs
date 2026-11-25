using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal enum CheckOutcome { Checked, SignInNeeded, Failed }

/// <summary>One child's result in a scheduled check; <paramref name="Arrivals"/> are the messages that check downloaded.</summary>
internal sealed record ChildCheck(string Child, CheckOutcome Outcome, IReadOnlyList<Message> Arrivals);

/// <summary>Localized notification templates, passed in so the composition does not depend on Android resources.</summary>
internal sealed record CheckTexts(
    string NewOne, string NewFew, string NewMany, string Arrival, string MoreOne, string MoreFew, string MoreMany,
    string NotCheckedSignIn, string NotCheckedFailed, string NoNewTitle, string NotCheckedTitle, string Checked, string ListAnd);

/// <summary>A notification line; <paramref name="Child"/> is the child label it starts with, shown in bold.</summary>
internal sealed record CheckLine(string Text, string? Child = null);

/// <summary>An arrival summary, or (<paramref name="IsStatus"/>) an empty-run status that replaces the previous one.</summary>
internal sealed record CheckNotification(bool IsStatus, string Title, IReadOnlyList<CheckLine> Lines);

internal static class ScheduledCheckReport
{
    /// <summary>The most arrival lines one notification lists; when more arrived, the last of them reads "+N more".</summary>
    internal const int MaxArrivalLines = 5;

    /// <summary>
    /// Inbox and Noticeboard messages a check downloaded that are not opened in the reader. Without an archive for
    /// the current school year, every unopened message counts; an account's very first download was just recorded
    /// as opened, as the reader's first load would, so it announces nothing.
    /// </summary>
    internal static IReadOnlyList<Message> Arrivals(Dictionary<string, List<Message>>? before,
        Dictionary<string, List<Message>> after, IReadOnlySet<string> opened)
    {
        HashSet<string> known = before is null ? [] : Ids(before);
        return after["received"].Concat(after["noticeboard"])
            .Where(message => !known.Contains(message.Id) && !opened.Contains(message.Id))
            .DistinctBy(message => message.Id).ToArray();
    }

    /// <summary>True when a sync added or removed messages, so a running reader should reload its archives.</summary>
    internal static bool Changed(Dictionary<string, List<Message>>? before, Dictionary<string, List<Message>> after) =>
        before is null || !Ids(before).SetEquals(Ids(after));

    /// <summary>
    /// One summary of every child's arrivals, newest first, followed by the children that could not be checked.
    /// Without arrivals, a status only when <paramref name="reportEmpty"/> is set; null when nothing should be posted.
    /// </summary>
    internal static CheckNotification? Compose(IReadOnlyList<ChildCheck> checks, bool reportEmpty, string time, CheckTexts texts)
    {
        var notChecked = checks.Where(check => check.Outcome != CheckOutcome.Checked)
            .Select(check => new CheckLine(Localized.Format(check.Outcome == CheckOutcome.SignInNeeded
                ? texts.NotCheckedSignIn : texts.NotCheckedFailed, check.Child)))
            .ToList();
        var arrivals = checks.SelectMany(check => check.Arrivals.Select(message => (check.Child, Message: message)))
            .OrderByDescending(arrival => arrival.Message.Date).ToList();
        if (arrivals.Count > 0)
        {
            var shown = arrivals.Count <= MaxArrivalLines ? arrivals.Count : MaxArrivalLines - 1;
            var lines = arrivals.Take(shown)
                .Select(arrival => new CheckLine(Localized.Format(texts.Arrival, arrival.Child, OneLine(arrival.Message.Title)), arrival.Child))
                .ToList();
            if (shown < arrivals.Count)
                lines.Add(new CheckLine(Localized.Plural(arrivals.Count - shown, texts.MoreOne, texts.MoreFew, texts.MoreMany)));
            return new(false, Localized.Plural(arrivals.Count, texts.NewOne, texts.NewFew, texts.NewMany), [.. lines, .. notChecked]);
        }
        if (!reportEmpty || checks.Count == 0) return null;
        var checkedChildren = checks.Where(check => check.Outcome == CheckOutcome.Checked).Select(check => check.Child).ToList();
        if (checkedChildren.Count == 0) return new(true, texts.NotCheckedTitle, notChecked);
        return new(true, texts.NoNewTitle,
            [new CheckLine(Localized.Format(texts.Checked, Names(checkedChildren, texts.ListAnd), time)), .. notChecked]);
    }

    private static string Names(IReadOnlyList<string> names, string and) => names.Count == 1 ? names[0] :
        Localized.Format(and, string.Join(", ", names.Take(names.Count - 1)), names[^1]);

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<string> Ids(Dictionary<string, List<Message>> folders) =>
        folders.Values.SelectMany(folder => folder).Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
}
