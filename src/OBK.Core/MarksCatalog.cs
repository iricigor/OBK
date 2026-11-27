using OBK.Core.Localization;

namespace OBK.Core;

public sealed record MarksAccount(Login Owner, MarksArchive? Archive, string? Error);

public sealed record MarksEntry(Login Owner, SubjectMarks Subject, MarksArchive Archive, Mark? Assessment = null)
{
    public string OwnerLabel => Owner.ChildName ?? Owner.SchoolName ?? Owner.Username;
    public string OwnerInitials => AvatarPresentation.Initials(OwnerLabel);
    public string OwnerColorKey => "Avatar" + AvatarPresentation.PaletteIndex(Owner.Url + "|" + Owner.Username);
    public string Key => Owner.Url + "\n" + Owner.Username + "\n" + Subject.Subject.Id;
    public string Title => Assessment is { Caption.Length: > 0 } mark ? mark.Caption :
        Assessment is null ? Subject.Subject.Name : SharedStrings.MarksAssessment;
    public string Subtitle => Assessment is null ? OwnerLabel : OwnerLabel + " · " + Subject.Subject.Name;
    public string Value => Assessment is { } mark
        ? mark.IsPoints && !string.IsNullOrWhiteSpace(mark.PointsText) ? mark.PointsText : mark.MarkText
        : string.IsNullOrWhiteSpace(Subject.AverageText) ? "—" : Subject.AverageText.Trim();
    public int NewCount => Assessment is { } mark ? Archive.IsNew(mark) ? 1 : 0 : Subject.Marks.Count(Archive.IsNew);
    public bool IsNew => NewCount > 0;
    public string CountText => Localized.Plural(Subject.Marks.Count,
        SharedStrings.MarksCountOne, SharedStrings.MarksCountFew, SharedStrings.MarksCountMany);
    public string Detail => Assessment is { } mark ? mark.Date.ToString(SharedStrings.DateShortFormat, LanguageSettings.Culture) +
        " · " + WeightText(mark) : CountText;
    public bool IsSubject => Assessment is null;
    public bool NoAssessments => Subject.Marks.Count == 0;
    public IReadOnlyList<MarksEntry> Preview => IsSubject ? Assessments.Take(4).ToArray() : [];
    public string AverageLabel => Subject.PointsOnly || Subject.Marks.Count > 0 && Subject.Marks.All(mark => mark.IsPoints)
        ? SharedStrings.MarksPoints : SharedStrings.MarksAverage;
    public string Provisional => string.IsNullOrWhiteSpace(Subject.TemporaryMark) ? "" :
        Localized.Format(SharedStrings.MarksProvisional, Subject.TemporaryMark);
    public string AccessibleName => string.Join(", ", new[] { Title, Subtitle, Value, Detail,
        IsNew ? SharedStrings.MarksNew : "" }.Where(part => part.Length > 0));
    public string Period => Archive.Semester is { } semester
        ? Localized.Format(SharedStrings.MarksPeriod,
            Mark.ParseDate(semester.From).ToString("d", LanguageSettings.Culture),
            Mark.ParseDate(semester.To).ToString("d", LanguageSettings.Culture))
        : SharedStrings.MarksCurrentPeriod;
    public string SnapshotText => Localized.Format(SharedStrings.MarksSynced,
        Mark.ParseDate(Archive.SyncedAt).ToLocalTime().ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture));
    public string AssessmentDetail => Assessment is { } mark ? string.Join(Environment.NewLine, new[]
    {
        Localized.Format(SharedStrings.MarksDate, mark.Date.ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture)),
        Localized.Format(SharedStrings.MarksEdited, mark.Edited.ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture)),
        WeightText(mark), mark.TypeNote ?? mark.Type,
        string.IsNullOrWhiteSpace(mark.TeacherId) ? null : Localized.Format(SharedStrings.MarksTeacher, mark.TeacherId),
        string.IsNullOrWhiteSpace(mark.Theme) ? null : Localized.Format(SharedStrings.MarksTopic, mark.Theme)
    }.Where(text => !string.IsNullOrWhiteSpace(text))) : "";
    public IReadOnlyList<MarksEntry> Assessments => Subject.Marks
        .OrderByDescending(mark => mark.Date).ThenByDescending(mark => mark.Edited)
        .ThenBy(mark => mark.Id, StringComparer.Ordinal)
        .Select(mark => this with { Assessment = mark }).ToArray();
    public static string WeightText(Mark mark) => mark.IsPoints ? SharedStrings.MarksPoints :
        mark.Weight is { } weight ? Localized.Format(SharedStrings.MarksWeight, weight) : SharedStrings.MarksWeightMissing;
}

public sealed class MarksCatalog(IReadOnlyList<MarksAccount> accounts)
{
    public IReadOnlyList<MarksAccount> Accounts { get; } = accounts;
    public int NewCount => Accounts.Sum(account => account.Archive?.Data.Subjects
        .Sum(subject => subject.Marks.Count(account.Archive.IsNew)) ?? 0);
    public string Notice => string.Join(Environment.NewLine, Accounts.Select(account =>
        account.Error is { } error ? Localized.Format(SharedStrings.ErrorInContext, Label(account.Owner), error) :
        account.Archive is null ? Localized.Format(SharedStrings.ErrorInContext, Label(account.Owner), SharedStrings.MarksNoData) : "")
        .Where(text => text.Length > 0));

    public static MarksCatalog Read(LocalStore store, IEnumerable<Login> scope) => new(scope.Select(owner =>
    {
        var safeOwner = owner with { Password = null };
        try { return new MarksAccount(safeOwner, store.LoadMarks(owner.Url, owner.Username), null); }
        catch (ObkException error) { return new MarksAccount(safeOwner, null, error.Message); }
    }).ToArray());

    private static string Label(Login owner) => owner.ChildName ?? owner.SchoolName ?? owner.Username;
    private IEnumerable<MarksEntry> AllSubjects() => Accounts.Where(account => account.Archive is not null)
        .SelectMany(account => account.Archive!.Data.Subjects.Select(subject => new MarksEntry(account.Owner, subject, account.Archive)));

    public IReadOnlyList<MarksEntry> Subjects(string search = "") => AllSubjects()
        .Where(entry => Matches(entry, search))
        .OrderBy(entry => entry.Subject.Subject.Name, StringComparer.Create(LanguageSettings.Culture, true))
        .ThenBy(entry => entry.OwnerLabel, StringComparer.Create(LanguageSettings.Culture, true))
        .ThenBy(entry => entry.Key, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<MarksEntry> Recent(string search = "") => AllSubjects()
        .SelectMany(entry => entry.Assessments).Where(entry => Matches(entry, search))
        .OrderByDescending(entry => entry.Assessment!.Date)
        .ThenByDescending(entry => entry.Assessment!.Edited)
        .ThenBy(entry => entry.Key, StringComparer.Ordinal)
        .ThenBy(entry => entry.Assessment!.Id, StringComparer.Ordinal).ToArray();

    private static bool Matches(MarksEntry entry, string search)
    {
        var marks = entry.Assessment is { } selected ? [selected] : entry.Subject.Marks.ToArray();
        var text = string.Join(" ", new[] { entry.Subject.Subject.Name, entry.Subject.Subject.Abbrev,
            entry.OwnerLabel, entry.Owner.Username, entry.Owner.SchoolName }
            .Concat(marks.SelectMany(mark => new[] { mark.Caption, mark.Theme, mark.MarkText, mark.PointsText })));
        return MessageInsights.Matches(text, search);
    }
}
