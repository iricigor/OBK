using OBK.Core;
using OBK.Core.Localization;
using System.Windows.Input;

namespace OBK.App;

public sealed class MarksViewModel : ObservableObject
{
    private readonly LocalStore store;
    private readonly Action<string> reportError;
    private MarksCatalog catalog = new([]);
    private IReadOnlyList<Login> scope = [];
    private IReadOnlyList<MarksEntry> entries = [];
    private MarksEntry? selectedEntry;
    private MarksEntry? selectedSubject;
    private MarksEntry? selectedAssessment;
    private string search = "";
    private string status = "";
    private bool recent;
    private bool busy;

    public MarksViewModel(LocalStore store, Action<string> reportError, Func<Task> refresh, Func<bool> available)
    {
        this.store = store;
        this.reportError = reportError;
        RefreshCommand = new AsyncCommand(_ => refresh(), reportError, _ => available());
        OpenEntryCommand = new AsyncCommand(p => p is MarksEntry entry ? OpenAsync(entry) : Task.CompletedTask,
            reportError, _ => !Busy);
        OpenAssessmentCommand = OpenEntryCommand;
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenEntryCommand { get; }
    public ICommand OpenAssessmentCommand { get; }
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsRecent { get => recent; set { if (Set(ref recent, value)) { Notify(nameof(IsSubjects)); Apply(); } } }
    public bool IsSubjects { get => !recent; set => IsRecent = !value; }
    public string Search { get => search; set { if (Set(ref search, value ?? "")) Apply(); } }
    public string Status { get => status; set => Set(ref status, value); }
    public string Notice => catalog.Notice;
    public bool HasNotice => Notice.Length > 0;
    public int NewCount => catalog.NewCount;
    public string ScopeText => scope.Count > 1 ? Localization.AppStrings.ScopeAllChildren + " · " + SharedStrings.MarksCurrentPeriod :
        scope.FirstOrDefault() is { } owner ? owner.ChildName ?? owner.SchoolName ?? owner.Username : SharedStrings.MarksNoAccounts;
    public IReadOnlyList<MarksEntry> Entries { get => entries; private set => Set(ref entries, value); }
    public MarksEntry? SelectedEntry
    {
        get => selectedEntry;
        set { if (Set(ref selectedEntry, value) && value is not null) Show(value); }
    }
    public MarksEntry? SelectedSubject { get => selectedSubject; private set => Set(ref selectedSubject, value); }
    public MarksEntry? SelectedAssessment { get => selectedAssessment; private set => Set(ref selectedAssessment, value); }
    public bool HasSubject => SelectedSubject is not null;
    public bool HasAssessment => SelectedAssessment is not null;
    public bool Empty => Entries.Count == 0;
    public string EmptyText => Search.Length > 0 ? SharedStrings.MarksNoMatches :
        scope.Count == 0 ? SharedStrings.MarksNoAccounts :
        catalog.Accounts.All(account => account.Archive is null) ? SharedStrings.MarksNoData : SharedStrings.MarksEmpty;

    public async Task LoadAsync(IReadOnlyList<Login> accounts)
    {
        Busy = true;
        try
        {
            scope = accounts.Select(owner => owner with { Password = null }).ToArray();
            catalog = await Task.Run(() => MarksCatalog.Read(store, scope));
            Apply();
            Notify(nameof(Notice));
            Notify(nameof(HasNotice));
            Notify(nameof(NewCount));
            Notify(nameof(ScopeText));
        }
        finally { Busy = false; }
    }

    private void Apply()
    {
        var subjectKey = SelectedSubject?.Key;
        var assessmentId = SelectedAssessment?.Assessment?.Id;
        Entries = IsRecent ? catalog.Recent(Search) : catalog.Subjects(Search);
        selectedEntry = Entries.FirstOrDefault(entry => entry.Key == subjectKey &&
            (!IsRecent || entry.Assessment?.Id == assessmentId));
        Notify(nameof(SelectedEntry));
        var subject = catalog.Subjects().FirstOrDefault(entry => entry.Key == subjectKey);
        // Keep a selection only if it still belongs to the filtered list.
        if (subject is not null && Entries.Any(entry => entry.Key == subject.Key)) Show(subject);
        else { SelectedSubject = null; SelectedAssessment = null; }
        if (subject is not null && SelectedSubject is not null)
            SelectedAssessment = subject.Assessments.FirstOrDefault(entry => entry.Assessment!.Id == assessmentId);
        Notify(nameof(HasSubject));
        Notify(nameof(HasAssessment));
        Notify(nameof(Empty));
        Notify(nameof(EmptyText));
    }

    private void Show(MarksEntry entry)
    {
        SelectedSubject = entry with { Assessment = null };
        SelectedAssessment = entry.Assessment is null ? null : entry;
        Notify(nameof(HasSubject));
        Notify(nameof(HasAssessment));
    }

    public void RestoreSelection(string? key, string? assessmentId)
    {
        SelectedEntry = Entries.FirstOrDefault(entry => entry.Key == key && (!IsRecent || entry.Assessment?.Id == assessmentId));
        if (SelectedEntry is not { } entry) return;
        Show(entry);
        if (!IsRecent)
            SelectedAssessment = entry.Assessments.FirstOrDefault(assessment => assessment.Assessment!.Id == assessmentId);
        Notify(nameof(HasAssessment));
    }

    public async Task OpenAsync(MarksEntry entry)
    {
        selectedEntry = Entries.FirstOrDefault(row => row.Key == entry.Key && (!IsRecent || row.Assessment?.Id == entry.Assessment?.Id));
        Notify(nameof(SelectedEntry));
        Show(entry);
        if (entry.Assessment is not { } mark) return;
        Busy = true;
        try
        {
            await Task.Run(() => store.AcknowledgeMark(entry.Owner.Url, entry.Owner.Username,
                mark.SubjectId, mark.Id, mark.Version));
        }
        catch (ObkException error) { Status = error.Message; reportError(error.Message); }
        finally { Busy = false; }
        await LoadAsync(scope);
    }
}
