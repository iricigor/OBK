using OBK.Core.Localization;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OBK.Core;

public enum MessageSort { Date, Sender, Title }
public enum MessageGrouping { None, Date, Sender, Title }

public sealed record MessageRef(Message Message, string Folder);
public sealed record RelatedMessage(MessageRef Item, double Score);
public sealed record RelatedMessages(IReadOnlyList<RelatedMessage> Previous, IReadOnlyList<RelatedMessage> Responses);

public static partial class MessageInsights
{
    public const string ExcuseType = "OMLUVENKA";

    /// <summary>Lower-case, diacritics-free, whitespace-collapsed text for matching.</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    public static bool IsExcuse(Message message) =>
        message.Extra is { } extra && extra.TryGetValue("Type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), ExcuseType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The other party: the sender of received items, the recipient of sent items.</summary>
    public static string Counterpart(Message message, string folder) =>
        folder == "sent" ? message.RelevantName ?? message.SenderName : message.SenderName;

    public static string SearchText(Message message, string folder)
    {
        var body = string.Concat(MessageRenderer.Render(message.Text).Select(p => p.Text));
        var date = message.Date;
        return Fold(string.Join('\n', message.Title, body, message.SenderName, message.RelevantName,
            date.ToString("dd MMM yyyy HH:mm"), date.ToString("d. M. yyyy", CultureInfo.InvariantCulture),
            date.ToString("d.M.yyyy", CultureInfo.InvariantCulture), date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    /// <summary>Every whitespace-separated query term must occur in the folded search text.</summary>
    public static bool Matches(string foldedSearchText, string? query)
    {
        var terms = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term => foldedSearchText.Contains(term, StringComparison.Ordinal));
    }

    public static string DateGroup(DateTimeOffset date, DateTime today)
    {
        var day = date.LocalDateTime.Date;
        today = today.Date;
        if (day >= today) return SharedStrings.GroupToday;
        if (day == today.AddDays(-1)) return SharedStrings.GroupYesterday;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (day >= weekStart) return SharedStrings.GroupThisWeek;
        if (day.Year == today.Year && day.Month == today.Month) return SharedStrings.GroupThisMonth;
        return day.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>Orders items for display and assigns each its group label; groups stay contiguous.</summary>
    public static IReadOnlyList<(T Item, string Group)> Arrange<T>(IEnumerable<T> items, Func<T, Message> message,
        Func<T, string> person, MessageSort sort, MessageGrouping grouping, DateTime today)
    {
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        var rows = items.Select(item => (Item: item, Message: message(item), Person: person(item))).ToList();
        IOrderedEnumerable<(T Item, Message Message, string Person)> Within(
            IEnumerable<(T Item, Message Message, string Person)> source) => sort switch
        {
            MessageSort.Sender => source.OrderBy(r => r.Person, comparer).ThenByDescending(r => r.Message.Date),
            MessageSort.Title => source.OrderBy(r => StripPrefixes(r.Message.Title), comparer).ThenByDescending(r => r.Message.Date),
            _ => source.OrderByDescending(r => r.Message.Date)
        };
        if (grouping == MessageGrouping.None)
            return Within(rows).Select(r => (r.Item, "")).ToArray();
        if (grouping == MessageGrouping.Date)
            return rows.OrderByDescending(r => r.Message.Date)
                .GroupBy(r => DateGroup(r.Message.Date, today))
                .SelectMany(g => Within(g).Select(r => (r.Item, g.Key)))
                .ToArray();
        string Label((T Item, Message Message, string Person) row) => grouping == MessageGrouping.Sender
            ? row.Person : StripPrefixes(row.Message.Title) is { Length: > 0 } title ? title : Localization.SharedStrings.NoSubject;
        return rows.GroupBy(r => Fold(Label(r)))
            .Select(g => (Label: Label(g.OrderByDescending(r => r.Message.Date).First()), Rows: g))
            .OrderBy(g => g.Label, comparer)
            .SelectMany(g => Within(g.Rows).Select(r => (r.Item, g.Label)))
            .ToArray();
    }

    public static string StripPrefixes(string? title) =>
        ReplyPrefix().Replace(MainSpace(title), "").Trim();

    private static string MainSpace(string? value) => Whitespace().Replace(value ?? "", " ").Trim();

    public static HashSet<string> SubjectTokens(string? title) =>
        Words().Matches(Fold(StripPrefixes(title))).Select(m => m.Value).Where(w => w.Length >= 2).ToHashSet();

    /// <summary>
    /// Heuristic subject similarity: shared words relative to the shorter subject, so added or changed
    /// middle parts (for example "Hodnocení / Evaluation") still match.
    /// </summary>
    public static double SubjectScore(string? a, string? b)
    {
        var left = Fold(StripPrefixes(a));
        var right = Fold(StripPrefixes(b));
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        var x = SubjectTokens(a);
        var y = SubjectTokens(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var common = x.Intersect(y).ToArray();
        var shorter = Math.Min(x.Count, y.Count);
        if (common.Length >= 2) return (double)common.Length / shorter;
        return shorter == 1 && common.Length == 1 && common[0].Length >= 5 ? 0.75 : 0;
    }

    private static HashSet<string> NameTokens(string? name) =>
        Fold(name).Split([' ', ',', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !t.Contains('.')).ToHashSet();

    /// <summary>Null when either name is missing, so callers can demand a stronger subject match.</summary>
    public static bool? SamePerson(string? a, string? b)
    {
        var x = NameTokens(a);
        var y = NameTokens(b);
        if (x.Count == 0 || y.Count == 0) return null;
        return x.Intersect(y).Count() >= Math.Min(2, Math.Min(x.Count, y.Count));
    }

    /// <summary>
    /// Finds possible earlier messages and responses. This is a heuristic: Komens lists do not expose
    /// reply links, so results must be presented as possible matches, not confirmed threads.
    /// When many messages share the subject and person (for example repeated excuses), a match also needs
    /// similar body text, or must be an adjacent message from the other side (Inbox vs Sent) with no
    /// same-subject message from the target's side between them.
    /// </summary>
    public static RelatedMessages FindRelated(MessageRef target, IEnumerable<MessageRef> all, int limit = 5)
    {
        if (target.Folder == "noticeboard") return new([], []);
        var targetExcuse = IsExcuse(target.Message);
        var person = Counterpart(target.Message, target.Folder);
        var pool = all.Where(c => c.Folder != "noticeboard").ToList();
        var matches = new List<RelatedMessage>();
        foreach (var candidate in pool)
        {
            if ((candidate.Folder == target.Folder && candidate.Message.Id == target.Message.Id) ||
                (targetExcuse && IsExcuse(candidate.Message))) continue;
            var score = SubjectScore(target.Message.Title, candidate.Message.Title);
            var same = SamePerson(person, Counterpart(candidate.Message, candidate.Folder));
            if (same == false || score < (same == true ? 0.6 : 0.8)) continue;
            matches.Add(new(candidate, score));
        }
        var date = target.Message.Date;
        var family = pool.Where(c => SubjectScore(target.Message.Title, c.Message.Title) >= 0.9 &&
            SamePerson(person, Counterpart(c.Message, c.Folder)) != false).ToList();
        if (family.Count >= AmbiguousSubjectCount)
        {
            var targetBody = BodyTokens(target.Message);
            bool Adjacent(MessageRef candidate)
            {
                if (candidate.Folder == target.Folder) return false;
                var other = candidate.Message.Date;
                var (from, to) = other < date ? (other, date) : (date, other);
                return !family.Any(x => x.Folder == target.Folder && x.Message.Date > from && x.Message.Date < to);
            }
            matches = matches.Where(m => BodySimilarity(targetBody, BodyTokens(m.Item.Message)) >= 0.5 || Adjacent(m.Item)).ToList();
        }
        return new(
            matches.Where(m => m.Item.Message.Date < date).OrderByDescending(m => m.Item.Message.Date).Take(limit).ToArray(),
            matches.Where(m => m.Item.Message.Date > date).OrderBy(m => m.Item.Message.Date).Take(limit).ToArray());
    }

    public const int AmbiguousSubjectCount = 3;

    private static HashSet<string> BodyTokens(Message message) =>
        Words().Matches(Fold(string.Concat(MessageRenderer.Render(message.Text).Select(p => p.Text))))
            .Select(m => m.Value).Where(w => w.Length >= 4).ToHashSet();

    /// <summary>Shared words relative to the shorter body; very short bodies never count as similar.</summary>
    public static double BodySimilarity(HashSet<string> a, HashSet<string> b)
    {
        var shorter = Math.Min(a.Count, b.Count);
        return shorter < 3 ? 0 : (double)a.Intersect(b).Count() / shorter;
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex Words();
    [GeneratedRegex(@"^(?:\s*(?:re|odp|odpoved|odpověď|fw|fwd|vs|sv|aw|wg|tr)\s*(?:\[\d+\]|\(\d+\))?\s*:\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();
}
