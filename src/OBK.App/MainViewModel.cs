using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace OBK.App;

/// <summary>Windows list grouping: the shared choices plus Child, offered only in the All children scope.</summary>
public enum ListGrouping { None, Date, Child, Sender, Title }

public sealed class MainViewModel : ObservableObject
{
    private static readonly Dictionary<string, List<Message>> NoMessages = Folders.Empty();
    private readonly LocalStore store;
    private readonly Action<bool, Login?> openLogin;
    private IReadOnlyList<ChildArchive> archives = [];
    private SchoolYear year = SchoolYear.Current();
    private string school = AppStrings.MainDefaultSchool;
    private Login? account;
    private bool showingAllChildren;
    private AllChildrenRow? allChildrenEntry;
    private bool syncing;
    private bool loading;
    private bool exporting;
    private InitialDownload? initialDownload;
    private InitialDownloadState? downloadState;
    private bool readingDuringDownload;
    private int refreshedAttachmentCount = -1;
    private bool attachmentRefreshQueued;
    private bool attachmentRefreshAll;
    private bool goodStatus;
    private bool listUpdating;
    private string status = AppStrings.StatusWelcome;
    private FolderRow? selectedFolder;
    private MessageRow? selectedMessage;
    private readonly Dictionary<string, MessageKey> selectedMessageIds = [];
    private IReadOnlyList<AccountRow> accounts = [];
    private IReadOnlyList<MessageRow> folderRows = [];
    private IReadOnlyList<MessageRow> messages = [];
    private ICollectionView? messagesView;
    private IReadOnlyList<AttachmentRow> attachments = [];
    private IReadOnlyList<TextPart> body = [];
    private IReadOnlyList<RelatedRow> related = [];
    private IReadOnlyList<DisplayOption<ListGrouping>> groupOptions = GroupChoices(false);
    private string subject = AppStrings.ReaderNoSelection;
    private string sender = AppStrings.ReaderNoSelectionSender;
    private string initials = "OB";
    private string metadata = AppStrings.ReaderNoSelectionDetail;
    private string searchText = "";
    private MessageSort sort = MessageSort.Date;
    private ListGrouping grouping = ListGrouping.None;
    private bool selectedIsExcuse;
    private bool navigationExpanded = true;
    private bool settingsOpen;
    private bool expanded;
    private int folderVersion;
    private int listVersion;
    private int messageVersion;

    /// <summary>One child's archive for the shown school year and the IDs of messages already opened on this PC.</summary>
    private sealed record ChildArchive(Login Login, bool Listed, bool PasswordSaved, Archive? Archive,
        HashSet<string> Opened, Dictionary<string, AttachmentInfo?> Info)
    {
        public Dictionary<string, List<Message>> Items => Archive?.Folders ?? NoMessages;
        public int NewIn(string folder) => CanBeNew(folder) ? Items[folder].Count(m => !Opened.Contains(m.Id)) : 0;
        public int NewCount => NewIn("received") + NewIn("noticeboard");
    }

    public MainViewModel(LocalStore store, ThemeManager theme, Action<bool, Login?> openLogin,
        Func<AttachmentRow, string, Task> export, Func<IReadOnlyList<AttachmentRow>, Task> saveAll)
    {
        this.store = store;
        this.openLogin = openLogin;
        Theme = theme;
        Marks = new MarksViewModel(store, ReportError, RefreshMarksAsync, () => CanChangeAccount);
        Marks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MarksViewModel.Busy)) UpdateActions();
            if (e.PropertyName == nameof(MarksViewModel.NewCount)) UpdateNewCounts();
        };
        RefreshCommand = new AsyncCommand(_ => RefreshAllAsync(), ReportError, _ => CanChangeAccount);
        ToggleThemeCommand = new AsyncCommand(_ => Theme.ToggleAsync(), ReportError);
        SetThemeCommand = new AsyncCommand(p => Theme.SetAsync(p as string ?? ""), ReportError);
        SetReadingSizeCommand = new AsyncCommand(p => Theme.SetReadingSizeAsync(p as string ?? ""), ReportError);
        SetListDensityCommand = new AsyncCommand(p => Theme.SetListDensityAsync(p as string ?? ""), ReportError);
        ToggleAlwaysRefreshAllChildrenCommand = new AsyncCommand(
            _ => Theme.SetAlwaysRefreshAllChildrenAsync(!Theme.AlwaysRefreshAllChildren), ReportError);
        OpenSettingsCommand = new RelayCommand(_ => SettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(_ => SettingsOpen = false);
        SwitchSchoolCommand = new RelayCommand(_ => openLogin(true, null), _ => CanChangeAccount);
        SelectAccountCommand = new AsyncCommand(p =>
            {
                SettingsOpen = false;
                return p is AccountRow { IsCurrent: false } row ? SelectAccountAsync(row) : Task.CompletedTask;
            }, ReportError, p => CanChangeAccount && p is AccountRow);
        SelectAllChildrenCommand = new AsyncCommand(_ =>
            {
                SettingsOpen = false;
                return showingAllChildren ? Task.CompletedTask : SelectAllChildrenAsync();
            }, ReportError, _ => CanChangeAccount);
        OpenRelatedCommand = new RelayCommand(p => { if (p is RelatedRow row) OpenRelated(row.Item); });
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
        ToggleNavigationCommand = new RelayCommand(_ => NavigationExpanded = !NavigationExpanded);
        OpenAttachmentCommand = ExportCommand("open", export);
        SaveAttachmentCommand = ExportCommand("save", export);
        OpenFolderCommand = ExportCommand("folder", export);
        SaveAllCommand = new AsyncCommand(_ => saveAll(attachments), ReportError,
            _ => CanUseAttachments && attachments.Count > 1 && attachments.All(a => a.Cached));
        ExpandAttachmentsCommand = new RelayCommand(_ => AttachmentsExpanded = !AttachmentsExpanded);
        Navigation =
        [
            new("noticeboard", Folders.Title("noticeboard"), "\uE789"),
            new("received", Folders.Title("received"), "\uE715"),
            new("sent", Folders.Title("sent"), "\uE724"),
            new("excuses", SharedStrings.FolderExcuses, "\uE8A5"),
            new("marks", SharedStrings.MarksTitle, "\uE9D5")
        ];
        selectedFolder = Navigation.Single(f => f.Key == "received");
        RefreshFolder();
    }

    private AsyncCommand ExportCommand(string action, Func<AttachmentRow, string, Task> export) =>
        new(p => p is AttachmentRow row ? export(row, action) : Task.CompletedTask, ReportError,
            p => CanUseAttachments && p is AttachmentRow { Cached: true });

    public ThemeManager Theme { get; }
    public MarksViewModel Marks { get; }
    public Func<Login, Task<string?>>? RequestMarksPassword { get; set; }
    public IReadOnlyList<DisplayOption<int>> RefreshIntervalOptions { get; } =
        RefreshIntervals.Minutes.Select(minutes => new DisplayOption<int>(minutes, RefreshIntervals.Label(minutes))).ToArray();
    public ICommand RefreshCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SetThemeCommand { get; }
    public ICommand SetReadingSizeCommand { get; }
    public ICommand SetListDensityCommand { get; }
    public ICommand ToggleAlwaysRefreshAllChildrenCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public bool SettingsOpen
    {
        get => settingsOpen;
        set { if (Set(ref settingsOpen, value)) { Notify(nameof(ShowMessages)); Notify(nameof(ShowMarks)); } }
    }
    public bool ShowMarks => !settingsOpen && selectedFolder?.Key == "marks";
    public bool ShowMessages => !settingsOpen && selectedFolder?.Key != "marks";
    public ICommand SwitchSchoolCommand { get; }
    public ICommand SelectAccountCommand { get; }
    public ICommand SelectAllChildrenCommand { get; }
    public ICommand OpenRelatedCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleNavigationCommand { get; }
    public bool NavigationExpanded { get => navigationExpanded; set => Set(ref navigationExpanded, value); }
    public ICommand OpenAttachmentCommand { get; }
    public ICommand SaveAttachmentCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SaveAllCommand { get; }
    public ICommand ExpandAttachmentsCommand { get; }
    public Task FolderReady { get; private set; } = Task.CompletedTask;
    public Task MessageReady { get; private set; } = Task.CompletedTask;
    /// <summary>Completes when every opened-message mark so far has been written to the local database.</summary>
    public Task OpenedSaved { get; private set; } = Task.CompletedTask;
    /// <summary>The last single child shown; it stays set while the All children scope is active.</summary>
    public Login? Account
    {
        get => account;
        private set
        {
            if (!Set(ref account, value)) return;
            Notify(nameof(AccountLabel));
            Notify(nameof(AccountColorKey));
            Notify(nameof(WindowTitle));
        }
    }
    public string School
    {
        get => school;
        private set
        {
            if (!Set(ref school, value)) return;
            Notify(nameof(AccountLabel));
            Notify(nameof(AccountColorKey));
            Notify(nameof(WindowTitle));
        }
    }
    public string AccountLabel => Account?.ChildName is { Length: > 0 } childName ? childName : School;
    public string AccountColorKey => Palette.PairKey(Account is { } a ? a.Url + "|" + a.Username : School);
    public string SenderColorKey => Palette.PairKey(Sender);
    public string WindowTitle => showingAllChildren ? Localized.Format(AppStrings.AppTitleWithAccount, AppStrings.ScopeAllChildren) :
        AccountLabel == AppStrings.MainDefaultSchool ? AppStrings.AppTitle : Localized.Format(AppStrings.AppTitleWithAccount, AccountLabel);
    /// <summary>True while the merged All children scope is shown; it needs two or more children.</summary>
    public bool ShowingAllChildren => showingAllChildren;
    public AllChildrenRow? AllChildrenEntry
    {
        get => allChildrenEntry;
        private set { if (Set(ref allChildrenEntry, value)) Notify(nameof(ShowAllChildrenEntry)); }
    }
    public bool ShowAllChildrenEntry => allChildrenEntry is not null;
    public string FolderTitle => selectedFolder?.Title ?? SharedStrings.FolderInbox;
    public string FolderCaption
    {
        get
        {
            if (selectedFolder?.Key == "marks") return Marks.ScopeText;
            var total = FolderItems(selectedFolder?.Key ?? "received").Count();
            var caption = SearchActive ? Localized.Format(AppStrings.MainFolderSearchCount, messages.Count, total) :
                Localized.Format(AppStrings.MainFolderCount, total);
            if (!SearchActive && selectedFolder is { HasNew: true } folder)
                caption = Localized.Format(AppStrings.MainFolderCaptionDetail, caption, Localized.Plural(folder.NewCount,
                    AppStrings.MainFolderNewOne, AppStrings.MainFolderNewFew, AppStrings.MainFolderNewMany));
            return showingAllChildren ? Localized.Format(AppStrings.MainFolderCaptionDetail, caption, AppStrings.ScopeAllChildren) : caption;
        }
    }
    public IReadOnlyList<FolderRow> Navigation { get; }
    public IReadOnlyList<AccountRow> Accounts { get => accounts; private set => Set(ref accounts, value); }
    public IReadOnlyList<MessageRow> Messages { get => messages; private set => Set(ref messages, value); }
    public ICollectionView? MessagesView { get => messagesView; private set => Set(ref messagesView, value); }
    public bool EmptyFolder => !Loading && messages.Count == 0;
    public string EmptyFolderText => SearchActive && folderRows.Count > 0
        ? Localized.Format(selectedFolder?.Key switch
        {
            "sent" => AppStrings.MainEmptySearchSent,
            "noticeboard" => AppStrings.MainEmptySearchNoticeboard,
            "excuses" => AppStrings.MainEmptySearchExcuses,
            _ => AppStrings.MainEmptySearchInbox
        }, SearchText.Trim())
        : selectedFolder?.Key switch
        {
            "sent" => AppStrings.MainEmptyFolderSent,
            "noticeboard" => AppStrings.MainEmptyFolderNoticeboard,
            "excuses" => AppStrings.MainEmptyFolderExcuses,
            _ => AppStrings.MainEmptyFolderInbox
        };
    public bool IsSyncing { get => syncing; private set { if (Set(ref syncing, value)) UpdateActions(); } }
    public bool Loading { get => loading; private set { if (Set(ref loading, value)) { UpdateActions(); Notify(nameof(EmptyFolder)); } } }
    public bool IsExporting { get => exporting; set { if (Set(ref exporting, value)) UpdateActions(); } }
    public bool CanChangeAccount => !IsSyncing && !Loading && !IsExporting && !Marks.Busy;
    public bool CanUseAttachments => !Loading && !IsExporting && (!IsSyncing || ReadingDuringDownload);
    public bool ReadingDuringDownload => readingDuringDownload;
    public InitialDownloadState? DownloadState
    {
        get => downloadState;
        private set
        {
            if (!Set(ref downloadState, value)) return;
            Notify(nameof(ShowDownloadStrip));
            Notify(nameof(DownloadActivity));
            Notify(nameof(DownloadNotice));
            foreach (var attachment in attachments) attachment.AvailabilityChanged();
            UpdateActions();
        }
    }
    public bool ShowDownloadStrip => ReadingDuringDownload && DownloadState is { Phase: InitialDownloadPhase.Attachments };
    public bool DownloadActivity => ShowDownloadStrip && DownloadState is { Running: true } && SystemParameters.ClientAreaAnimation;
    public string DownloadNotice => DownloadState is { Error: not null } state ? state.Status : SharedStrings.DownloadKeepOpen;
    public Task AttachmentAvailabilityReady { get; private set; } = Task.CompletedTask;
    public bool AttachmentsPendingFor(Login owner) => initialDownload is { } download &&
        Same(download.Account, owner) && download.State is { MessagesReady: true, Running: true };
    public string Status { get => status; private set => Set(ref status, value); }
    public bool GoodStatus { get => goodStatus; private set => Set(ref goodStatus, value); }
    public string Subject { get => subject; private set => Set(ref subject, value); }
    public string Sender { get => sender; private set { if (Set(ref sender, value)) Notify(nameof(SenderColorKey)); } }
    public string Initials { get => initials; private set => Set(ref initials, value); }
    public string Metadata { get => metadata; private set => Set(ref metadata, value); }
    public bool SelectedIsExcuse { get => selectedIsExcuse; private set => Set(ref selectedIsExcuse, value); }
    public IReadOnlyList<TextPart> Body { get => body; private set => Set(ref body, value); }
    public IReadOnlyList<RelatedRow> Related
    {
        get => related;
        private set { if (Set(ref related, value)) Notify(nameof(HasRelated)); }
    }
    public bool HasRelated => related.Count > 0;
    public bool HasAttachments => attachments.Count > 0;
    public bool MultipleAttachments => attachments.Count > 1;
    public IReadOnlyList<AttachmentRow> VisibleAttachments => attachments.Count > 4 && !expanded ? attachments.Take(3).ToArray() : attachments;
    public bool HasMoreAttachments => attachments.Count > 4;
    public string MoreAttachmentsLabel => expanded ? AppStrings.AttachmentsShowFewer :
        Localized.Format(AppStrings.AttachmentsMore, attachments.Count - 3);
    public bool AttachmentsExpanded
    {
        get => expanded;
        private set { if (Set(ref expanded, value)) { Notify(nameof(VisibleAttachments)); Notify(nameof(MoreAttachmentsLabel)); } }
    }

    public IReadOnlyList<DisplayOption<MessageSort>> SortOptions { get; } = Enum.GetValues<MessageSort>()
        .Select(value => new DisplayOption<MessageSort>(value, value switch
        {
            MessageSort.Date => AppStrings.MainSortOptionDate,
            MessageSort.Sender => AppStrings.MainSortOptionSender,
            MessageSort.Title => AppStrings.MainSortOptionTitle,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        })).ToArray();
    public IReadOnlyList<DisplayOption<ListGrouping>> GroupOptions { get => groupOptions; private set => Set(ref groupOptions, value); }
    public bool SearchActive => SearchText.Trim().Length > 0;
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!Set(ref searchText, value ?? "")) return;
            Notify(nameof(SearchActive));
            ApplyList();
        }
    }
    public MessageSort Sort { get => sort; set { if (Set(ref sort, value)) ApplyList(); } }
    public ListGrouping Grouping { get => grouping; set { if (Set(ref grouping, value)) ApplyList(); } }

    public FolderRow? SelectedFolder
    {
        get => selectedFolder;
        set
        {
            if (!Set(ref selectedFolder, value) || value is null) return;
            SettingsOpen = false;
            Notify(nameof(ShowMessages));
            Notify(nameof(ShowMarks));
            RefreshFolder();
        }
    }
    /// <summary>
    /// Set by the list when the user clicks or keyboard-selects a message, which marks it opened on this PC.
    /// Automatic selections (opening a folder, refreshing, changing scope or search) never mark messages opened.
    /// </summary>
    public MessageRow? SelectedMessage
    {
        get => selectedMessage;
        set { if (SelectMessage(value) && !listUpdating && value is not null) MarkOpened(value); }
    }

    private bool SelectMessage(MessageRow? value)
    {
        if (!Set(ref selectedMessage, value, nameof(SelectedMessage))) return false;
        if (value is not null && selectedFolder is { } folder) selectedMessageIds[folder.Key] = value.Key;
        RefreshMessage();
        return true;
    }

    private static IReadOnlyList<DisplayOption<ListGrouping>> GroupChoices(bool child) => Enum.GetValues<ListGrouping>()
        .Where(value => child || value != ListGrouping.Child)
        .Select(value => new DisplayOption<ListGrouping>(value, value switch
        {
            ListGrouping.None => AppStrings.MainGroupOptionNone,
            ListGrouping.Date => AppStrings.MainGroupOptionDate,
            ListGrouping.Child => AppStrings.MainGroupOptionChild,
            ListGrouping.Sender => AppStrings.MainGroupOptionSender,
            ListGrouping.Title => AppStrings.MainGroupOptionTitle,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        })).ToArray();

    private void UpdateActions()
    {
        Notify(nameof(CanChangeAccount));
        Notify(nameof(CanUseAttachments));
        CommandManager.InvalidateRequerySuggested();
    }
    public void SetStatus(string text, bool ready = false) { Status = text; GoodStatus = ready; }
    public void ReportError(string text) => SetStatus(text);
    public static string OneLine(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();

    public async Task InitializeAsync()
    {
        string? themeError = null;
        try { await Theme.InitializeAsync(); }
        catch (ObkException error) { themeError = error.Message; }
        try
        {
            var last = await Task.Run(store.LastAccount);
            await LoadAsync(last is null ? null : Normalize(last.Url, last.Username, last.ChildName, last.SchoolName), year);
        }
        catch (ObkException error) { ReportError(error.Message); }
        if (themeError is not null) ReportError(themeError);
    }

    /// <summary>Makes this the current child and reloads every child's archive; the remembered scope is kept.</summary>
    public async Task ActivateAccountAsync(string url, string username, SchoolYear? selectedYear = null,
        string? childName = null, string? schoolName = null) =>
        await LoadAsync(Normalize(url, username, childName, schoolName), selectedYear ?? year);

    private static Login Normalize(string url, string username, string? childName, string? schoolName) =>
        new(KomensClient.NormalizeUrl(url), username, ChildName: childName?.Trim() is { Length: > 0 } child ? child : null,
            SchoolName: schoolName?.Trim() is { Length: > 0 } schoolLabel ? schoolLabel : null);

    private static bool Same(Login? left, Login? right) =>
        left is not null && right is not null && left.Url == right.Url && left.Username == right.Username;

    private static bool CanBeNew(string folder) => folder is "received" or "noticeboard";

    private string ScopeKey => showingAllChildren ? "*" : account is { } current ? current.Url + "\n" + current.Username : "";

    private IEnumerable<ChildArchive> ScopeArchives() => showingAllChildren ? archives.Where(child => child.Listed) :
        archives.Where(child => Same(child.Login, account));

    private bool HasArchive => ScopeArchives().Any(child => child.Archive is not null);

    private async Task LoadAsync(Login? next, SchoolYear nextYear)
    {
        if (Loading || IsExporting) throw new ObkException(AppStrings.StatusWaitForArchive);
        Loading = true;
        IReadOnlyList<string> errors;
        try
        {
            // Opened marks still being written must not be read back as new.
            await OpenedSaved;
            (var loaded, errors) = await Task.Run(() => ReadArchives(next, nextYear));
            // Opened state only grows; keep marks made while the archives were being read.
            foreach (var child in loaded)
                if (archives.FirstOrDefault(old => Same(old.Login, child.Login)) is { } old && child.Archive is not null)
                    child.Opened.UnionWith(old.Opened);
            var previous = (ScopeKey, year);
            Account = next;
            year = nextYear;
            archives = loaded;
            School = next?.SchoolName ?? loaded.FirstOrDefault(child => Same(child.Login, next))?.Archive?.SchoolName ??
                AppStrings.MainDefaultSchool;
            UpdateScope();
            await Marks.LoadAsync(ScopeArchives().Select(child => child.Login).ToArray());
            if ((ScopeKey, year) != previous)
            {
                selectedMessageIds.Clear();
                searchText = "";
                Notify(nameof(SearchText));
                Notify(nameof(SearchActive));
            }
            RefreshFolder();
            if (next is not null || showingAllChildren) SetStatus(CacheStatus(), HasArchive);
        }
        finally { Loading = false; }
        if (errors.Count > 0) ReportError(string.Join("; ", errors));
    }

    /// <summary>
    /// Reads every known child's archive. A child whose archive cannot be read is shown without one and
    /// reported, so the other children stay readable.
    /// </summary>
    private (IReadOnlyList<ChildArchive> Archives, IReadOnlyList<string> Errors) ReadArchives(Login? current, SchoolYear archiveYear)
    {
        var known = store.KnownLogins();
        var entries = known.Select(login => (Login: login, Listed: true)).ToList();
        if (current is not null && !known.Any(login => Same(login, current))) entries.Add((current, false));
        var errors = new List<string>();
        var result = entries.Select(entry =>
        {
            var login = entry.Login with { Password = null };
            var saved = entry.Login.Password is not null;
            try
            {
                var archive = store.LoadArchive(login.Url, login.Username, archiveYear);
                if (archive is null) return new ChildArchive(login, entry.Listed, saved, null, [], []);
                var opened = store.OpenedMessages(login.Url, login.Username,
                    archive.Folders.Values.SelectMany(messages => messages).Select(message => message.Id));
                return new ChildArchive(login, entry.Listed, saved, archive, opened,
                    ReadAttachmentInfo(login.Url, login.Username, archive.Folders));
            }
            catch (ObkException error)
            {
                errors.Add(Localized.Format(SharedStrings.ErrorInContext, AccountRow.LabelFor(login), error.Message));
                return new ChildArchive(login, entry.Listed, saved, null, [], []);
            }
        }).ToArray();
        return (result, errors);
    }

    private void UpdateScope()
    {
        var all = Theme.AllChildren != false && archives.Count(child => child.Listed) >= 2;
        if (showingAllChildren != all)
        {
            showingAllChildren = all;
            Notify(nameof(ShowingAllChildren));
            Notify(nameof(WindowTitle));
        }
        if (!all && grouping == ListGrouping.Child)
        {
            grouping = ListGrouping.None;
            Notify(nameof(Grouping));
        }
        if (groupOptions.Any(option => option.Value == ListGrouping.Child) != all) GroupOptions = GroupChoices(all);
        BuildAccounts();
    }

    private void BuildAccounts()
    {
        var comparer = StringComparer.Create(LanguageSettings.Culture, true);
        Accounts = archives.Where(child => child.Listed)
            .Select(child => new AccountRow(child.Login, !showingAllChildren && Same(child.Login, account), child.PasswordSaved))
            .OrderBy(row => row.Label, comparer).ThenBy(row => row.Login.Url, StringComparer.Ordinal)
            .ThenBy(row => row.Login.Username, StringComparer.Ordinal).ToArray();
        AllChildrenEntry = accounts.Count >= 2 ? new AllChildrenRow(showingAllChildren, accounts) : null;
        UpdateNewCounts();
    }

    /// <summary>Badges count new messages per child (All children sums them) and per folder in the current scope.</summary>
    private void UpdateNewCounts()
    {
        foreach (var row in accounts) row.NewCount = archives.FirstOrDefault(child => Same(child.Login, row.Login))?.NewCount ?? 0;
        if (allChildrenEntry is { } entry) entry.NewCount = accounts.Sum(row => row.NewCount);
        var scope = ScopeArchives().ToArray();
        foreach (var folder in Navigation)
            folder.NewCount = folder.Key == "marks" ? Marks.NewCount : scope.Sum(child => child.NewIn(folder.Key));
        Notify(nameof(FolderCaption));
    }

    private async Task ReloadAccountsAsync()
    {
        try
        {
            var known = await Task.Run(store.KnownLogins);
            archives = archives.Select(child => child with
            {
                PasswordSaved = known.Any(login => Same(login, child.Login) && login.Password is not null)
            }).ToArray();
            BuildAccounts();
        }
        catch (ObkException error) { ReportError(error.Message); }
    }

    /// <summary>Shows one child's archive and remembers that choice instead of All children.</summary>
    public async Task SelectAccountAsync(AccountRow row)
    {
        var login = row.Login;
        await Task.Run(() => store.SaveAccount(login.Url, login.Username, login.ChildName, login.SchoolName));
        var saving = Theme.SetAllChildrenAsync(false);
        await ActivateAccountAsync(login.Url, login.Username, SchoolYear.Current(), login.ChildName, login.SchoolName);
        await saving;
    }

    /// <summary>Merges every child's archive and remembers the All children scope.</summary>
    public async Task SelectAllChildrenAsync()
    {
        var saving = Theme.SetAllChildrenAsync(true);
        await LoadAsync(Account, SchoolYear.Current());
        await saving;
    }

    public async Task RenameAccountAsync(AccountRow row, string? childName)
    {
        if (!CanChangeAccount) throw new ObkException(AppStrings.StatusWaitForOperation);
        var login = row.Login;
        var label = childName?.Trim() is { Length: > 0 } name ? name : null;
        await Task.Run(() => store.RenameAccount(login.Url, login.Username, label));
        if (Account is { } current && Same(current, login)) Account = current with { ChildName = label };
        archives = archives.Select(child => Same(child.Login, login) ? child with { Login = child.Login with { ChildName = label } } : child)
            .ToArray();
        BuildAccounts();
        await Marks.LoadAsync(ScopeArchives().Select(child => child.Login).ToArray());
        if (showingAllChildren) RefreshFolder();
        SetStatus(label is null ? Localized.Format(AppStrings.StatusChildNameRemoved, login.SchoolName ?? login.Username) :
            Localized.Format(AppStrings.StatusRenamed, label), true);
    }

    /// <summary>Signs in once to verify the password, then remembers it; nothing is saved when sign-in fails.</summary>
    public async Task VerifyAndRememberPasswordAsync(AccountRow row, string password)
    {
        if (!CanChangeAccount) throw new ObkException(AppStrings.StatusWaitForOperation);
        if (password.Length == 0) throw new ObkException(AppStrings.ErrorEnterPassword);
        var login = row.Login;
        await Task.Run(async () =>
        {
            using var client = new KomensClient(login.Url);
            await client.SignInAsync(login.Username, password);
            store.RememberPassword(login.Url, login.Username, password);
        });
        await ReloadAccountsAsync();
        SetStatus(Localized.Format(AppStrings.StatusPasswordSaved, row.Label), true);
    }

    public async Task ForgetPasswordAsync(AccountRow row)
    {
        if (!CanChangeAccount) throw new ObkException(AppStrings.StatusWaitForOperation);
        await Task.Run(() => store.ForgetPassword(row.Login.Url, row.Login.Username));
        await ReloadAccountsAsync();
        SetStatus(Localized.Format(AppStrings.StatusPasswordCleared, row.Label), true);
    }

    private Dictionary<string, AttachmentInfo?> ReadAttachmentInfo(string url, string username, Dictionary<string, List<Message>> items) =>
        items.Values.SelectMany(m => m).SelectMany(m => m.Attachments).DistinctBy(a => a.Identifier)
            .ToDictionary(a => a.Identifier, a => store.AttachmentInfo(url, username, a.Identifier));

    /// <summary>
    /// One child's archive totals, or in the All children scope how many children are listed, the oldest sync and
    /// which children have no downloaded archive yet.
    /// </summary>
    private string CacheStatus()
    {
        var loaded = ScopeArchives().Where(child => child.Archive is not null).ToArray();
        if (loaded.Length == 0) return AppStrings.StatusNoArchive;
        static string When(string stamp) =>
            DateTimeOffset.Parse(stamp).ToLocalTime().ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture);
        if (showingAllChildren)
        {
            var children = ScopeArchives().ToArray();
            var status = Localized.Format(AppStrings.StatusArchiveReadyAllChildren,
                Localized.Plural(children.Length, AppStrings.StatusChildCountOne, AppStrings.StatusChildCountFew, AppStrings.StatusChildCountMany),
                When(loaded.MinBy(child => DateTimeOffset.Parse(child.Archive!.SyncedAt))!.Archive!.SyncedAt));
            var missing = children.Where(child => child.Archive is null).Select(child => AccountRow.LabelFor(child.Login)).ToArray();
            return missing.Length == 0 ? status : Localized.Format(AppStrings.StatusChildrenNotDownloaded, status, string.Join(", ", missing));
        }
        var single = loaded[0];
        return Localized.Format(AppStrings.StatusArchiveReady, single.Items.Values.Sum(m => m.Count),
            single.Info.Values.Count(a => a is not null), When(single.Archive!.SyncedAt));
    }

    public sealed record ReaderState(Login? Account, SchoolYear Year, string Folder, string Search,
        MessageSort Sort, ListGrouping Grouping, Dictionary<string, MessageKey> MessageIds,
        bool NavigationExpanded, bool SettingsOpen, bool AttachmentsExpanded,
        bool MarksRecent = false, string MarksSearch = "", string? MarksSelectionKey = null, string? MarksAssessmentId = null);

    public ReaderState CaptureState() => new(Account, year, SelectedFolder?.Key ?? "received", SearchText,
        Sort, Grouping, new(selectedMessageIds), NavigationExpanded, SettingsOpen, AttachmentsExpanded, Marks.IsRecent, Marks.Search,
        Marks.SelectedSubject?.Key, Marks.SelectedAssessment?.Assessment?.Id);

    public async Task RestoreStateAsync(ReaderState state)
    {
        await LoadAsync(state.Account, state.Year);
        await FolderReady;
        selectedMessageIds.Clear();
        foreach (var (key, id) in state.MessageIds) selectedMessageIds[key] = id;
        searchText = state.Search;
        sort = state.Sort;
        grouping = state.Grouping == ListGrouping.Child && !showingAllChildren ? ListGrouping.None : state.Grouping;
        selectedFolder = Navigation.Single(folder => folder.Key == state.Folder);
        Notify(nameof(SearchText));
        Notify(nameof(SearchActive));
        Notify(nameof(Sort));
        Notify(nameof(Grouping));
        Notify(nameof(SelectedFolder));
        Notify(nameof(ShowMessages));
        Notify(nameof(ShowMarks));
        Marks.IsRecent = state.MarksRecent;
        Marks.Search = state.MarksSearch;
        Marks.RestoreSelection(state.MarksSelectionKey, state.MarksAssessmentId);
        FolderReady = RefreshFolderAsync();
        await FolderReady;
        await MessageReady;
        NavigationExpanded = state.NavigationExpanded;
        SettingsOpen = state.SettingsOpen;
        AttachmentsExpanded = state.AttachmentsExpanded;
    }

    public void OfflineStatus() => SetStatus(HasArchive ? Localized.Format(AppStrings.StatusOffline, CacheStatus()) : CacheStatus(),
        HasArchive);

    /// <summary>Excuses are a view over Sent, split by the API message type; storage keeps the server folders.</summary>
    private static IEnumerable<Message> FolderMessages(Dictionary<string, List<Message>> items, string key) => key switch
    {
        "excuses" => items["sent"].Where(MessageInsights.IsExcuse),
        "sent" => items["sent"].Where(m => !MessageInsights.IsExcuse(m)),
        _ => items[key]
    };

    private IEnumerable<(ChildArchive Child, Message Message)> FolderItems(string key) =>
        ScopeArchives().SelectMany(child => FolderMessages(child.Items, key).Select(message => (child, message)));

    /// <summary>Records that the user opened a message on this PC; the school's read state is never changed.</summary>
    public void MarkOpened(MessageRow row) => MarkOpened(row.Owner, row.Folder, row.Message.Id);

    private void MarkOpened(Login owner, string folder, string id)
    {
        if (!CanBeNew(folder) || archives.FirstOrDefault(child => Same(child.Login, owner)) is not { } archive) return;
        foreach (var row in folderRows)
            if (row.IsNew && row.Message.Id == id && Same(row.Owner, owner)) row.IsNew = false;
        if (!archive.Opened.Add(id)) return;
        UpdateNewCounts();
        OpenedSaved = SaveOpenedAsync(OpenedSaved, owner, id);
    }

    private async Task SaveOpenedAsync(Task previous, Login owner, string id)
    {
        await previous;
        try { await Task.Run(() => store.MarkOpened(owner.Url, owner.Username, id)); }
        catch (ObkException error) { ReportError(error.Message); }
    }

    /// <summary>Related messages come from the displayed message's own child, so the link opens in that child's archive.</summary>
    private void OpenRelated(MessageRef reference)
    {
        if (selectedMessage is not { } current) return;
        var owner = current.Owner;
        var item = reference.Folder == "sent" && MessageInsights.IsExcuse(reference.Message) ? reference with { Folder = "excuses" } : reference;
        selectedMessageIds[item.Folder] = new(owner.Url, owner.Username, item.Message.Id);
        MarkOpened(owner, reference.Folder, item.Message.Id);
        if (SearchActive) { searchText = ""; Notify(nameof(SearchText)); Notify(nameof(SearchActive)); }
        if (selectedFolder?.Key == item.Folder) RefreshFolder();
        else SelectedFolder = Navigation.Single(f => f.Key == item.Folder);
    }

    private async void RefreshFolder()
    {
        FolderReady = RefreshFolderAsync();
        await FolderReady;
    }

    private async Task RefreshFolderAsync()
    {
        var version = ++folderVersion;
        var key = selectedFolder?.Key ?? "received";
        Notify(nameof(FolderTitle));
        listUpdating = true;
        try
        {
            SelectMessage(null);
            ClearMessage();
            folderRows = [];
            Messages = [];
            MessagesView = null;
        }
        finally { listUpdating = false; }
        Notify(nameof(FolderCaption));
        Notify(nameof(EmptyFolderText));
        Notify(nameof(EmptyFolder));
        if (key == "marks") return;
        try
        {
            var source = FolderItems(key).ToArray();
            var rowFolder = key == "excuses" ? "sent" : key;
            var showOwner = showingAllChildren;
            var rows = await Task.Run(() => source.Select(item =>
                new MessageRow(item.Message, rowFolder, item.Child.Login, showOwner)).ToArray());
            if (version != folderVersion) return;
            for (var index = 0; index < rows.Length; index++)
                rows[index].IsNew = CanBeNew(rowFolder) && !source[index].Child.Opened.Contains(rows[index].Message.Id);
            folderRows = rows;
            await ApplyListAsync(version);
        }
        catch (ObkException error) { if (version == folderVersion) ReportError(error.Message); }
    }

    private async void ApplyList()
    {
        var version = folderVersion;
        FolderReady = ApplyListAsync(version);
        try { await FolderReady; }
        catch (ObkException error) { ReportError(error.Message); }
    }

    private async Task ApplyListAsync(int version)
    {
        var listId = ++listVersion;
        var key = selectedFolder?.Key ?? "received";
        var rows = folderRows;
        var query = SearchText;
        var (sortBy, groupBy) = (Sort, Grouping);
        var children = accounts.Select(row => (row.Login, row.Label)).ToArray();
        var arranged = await Task.Run(() =>
        {
            var matching = rows.Where(r => MessageInsights.Matches(r.SearchText, query)).ToArray();
            // Child groups follow the sidebar order; the chosen sort applies within each child.
            if (groupBy == ListGrouping.Child)
                return children.SelectMany(child => MessageInsights.Arrange(matching.Where(r => Same(r.Owner, child.Login)),
                        r => r.Message, r => r.Person, sortBy, MessageGrouping.None, DateTime.Today)
                    .Select(a => (a.Item, Group: child.Label))).ToArray();
            return MessageInsights.Arrange(matching, r => r.Message, r => r.Person, sortBy, groupBy switch
            {
                ListGrouping.Date => MessageGrouping.Date,
                ListGrouping.Sender => MessageGrouping.Sender,
                ListGrouping.Title => MessageGrouping.Title,
                _ => MessageGrouping.None
            }, DateTime.Today).ToArray();
        });
        if (version != folderVersion || listId != listVersion) return;
        for (var index = 0; index < arranged.Length; index++)
        {
            var (row, group) = arranged[index];
            row.Group = group;
            row.IsAlternate = index % 2 == 1;
        }
        var list = arranged.Select(a => a.Item).ToList();
        var view = new ListCollectionView(list);
        if (groupBy != ListGrouping.None) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MessageRow.Group)));
        var previous = selectedMessage;
        selectedMessageIds.TryGetValue(key, out var remembered);
        // The list control may write its selection back while its items are replaced; that is not the user's choice.
        listUpdating = true;
        try
        {
            Messages = list;
            MessagesView = view;
            var selected = list.FirstOrDefault(r => ReferenceEquals(r, previous)) ??
                list.FirstOrDefault(r => r.Key == remembered) ??
                (query.Trim().Length > 0 ? list.FirstOrDefault() : list.MaxBy(r => r.Message.Date));
            if (!ReferenceEquals(selected, selectedMessage)) SelectMessage(selected);
            else Notify(nameof(SelectedMessage));
        }
        finally { listUpdating = false; }
        Notify(nameof(FolderCaption));
        Notify(nameof(EmptyFolderText));
        Notify(nameof(EmptyFolder));
    }

    private void ClearMessage()
    {
        ++messageVersion;
        Subject = AppStrings.ReaderNoSelection;
        Sender = AppStrings.ReaderNoSelectionSender;
        Initials = "OB";
        Metadata = AppStrings.ReaderNoSelectionDetail;
        SelectedIsExcuse = false;
        Body = [];
        Related = [];
        SetAttachments([]);
    }

    private async void RefreshMessage()
    {
        MessageReady = RefreshMessageAsync();
        await MessageReady;
    }

    private async Task RefreshMessageAsync()
    {
        ClearMessage();
        if (selectedMessage is not { } row) return;
        var version = messageVersion;
        var key = row.Folder;
        var owner = row.Owner;
        var child = archives.FirstOrDefault(archive => Same(archive.Login, owner));
        var all = child?.Items ?? NoMessages;
        Subject = row.Subject;
        Sender = OneLine(row.Message.SenderName) is { Length: > 0 } name ? name : AppStrings.ReaderNotSupplied;
        var words = Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Contains('.')).ToArray();
        Initials = string.IsNullOrWhiteSpace(row.Message.SenderName) || words.Length == 0 ? "?" :
            string.Concat(words.TakeLast(2).Select(w => w[0])).ToUpperInvariant();
        SelectedIsExcuse = row.IsExcuse;
        Metadata = row.ShowOwner ? Localized.Format(AppStrings.ReaderMetadataChild, row.Sent, FolderTitle, row.OwnerLabel) :
            Localized.Format(AppStrings.ReaderMetadata, row.Sent, FolderTitle);
        if (key == "sent" && !string.IsNullOrEmpty(row.Message.RelevantName))
            Metadata += "\n" + Localized.Format(AppStrings.ReaderRecipient, OneLine(row.Message.RelevantName));
        if (row.Message.CanConfirm == true && row.Message.Confirmed != true)
            Metadata += "\n" + AppStrings.ReaderConfirmationRequested;
        try
        {
            var result = await Task.Run(() =>
            {
                var parts = MessageRenderer.Render(row.Message.Text);
                var files = row.Message.Attachments.Select(a =>
                {
                    _ = a.SafeName;
                    return new AttachmentRow(this, a, store.AttachmentInfo(owner.Url, owner.Username, a.Identifier), owner);
                }).ToArray();
                // Possibly related messages are searched only in the owning child's archive, never across children.
                var related = MessageInsights.FindRelated(new(row.Message, key),
                    all.SelectMany(f => f.Value.Select(m => new MessageRef(m, f.Key))));
                return (parts, files, related);
            });
            if (version != messageVersion) return;
            Body = result.parts.Count == 0 ? [new(AppStrings.ReaderNoContent, false, false, false, false)] : result.parts;
            SetAttachments(result.files);
            Related = RelatedRow.Build(this, result.related, row.Message.Date, DateTime.Today);
        }
        catch (ObkException error) { if (version == messageVersion) ReportError(error.Message); }
    }

    private void SetAttachments(IReadOnlyList<AttachmentRow> value)
    {
        attachments = value;
        expanded = false;
        Notify(nameof(VisibleAttachments));
        Notify(nameof(HasAttachments));
        Notify(nameof(MultipleAttachments));
        Notify(nameof(HasMoreAttachments));
        Notify(nameof(MoreAttachmentsLabel));
        Notify(nameof(AttachmentsExpanded));
        UpdateActions();
    }

    /// <summary>Downloads one account's school year into the local store and returns the school's name.</summary>
    private async Task<string> DownloadAsync(KomensClient client, string token, string url, string username,
        SchoolYear syncYear, bool fullRefresh, IProgress<string> progress) =>
        await Task.Run(async () =>
        {
            var schoolName = await client.SchoolNameAsync(token);
            var cached = fullRefresh ? null : store.LoadArchive(url, username, syncYear);
            var fetched = await client.FetchYearAsync(token, syncYear, cached?.Folders, progress);
            await client.DownloadAttachmentsAsync(token, fetched, store, username, progress);
            store.SaveArchive(url, username, syncYear, schoolName, fetched);
            return schoolName;
        });

    /// <summary>Creates the sign-in client; the smoke test substitutes an in-memory fictional school.</summary>
    internal Func<string, KomensClient> ClientFactory { get; set; } = url => new KomensClient(url);

    public async Task SynchronizeAsync(string url, string username, string password, bool remember, bool fullRefresh,
        string? childName, string? schoolName, Action<InitialDownloadState> loginProgress)
    {
        using var client = ClientFactory(url);
        var download = new InitialDownload(new(url, username, ChildName: childName, SchoolName: schoolName), SchoolYear.Current());
        await SynchronizeAsync(download, client, password, remember, fullRefresh, loginProgress);
    }

    private async Task SynchronizeAsync(InitialDownload download, KomensClient client, string password, bool remember,
        bool fullRefresh, Action<InitialDownloadState> loginProgress)
    {
        if (!CanChangeAccount) throw new ObkException(AppStrings.StatusWaitForOperation);
        initialDownload = download;
        readingDuringDownload = false;
        refreshedAttachmentCount = -1;
        DownloadState = download.State;
        IsSyncing = true;
        IProgress<InitialDownloadState> progress = new Progress<InitialDownloadState>(_ =>
        {
            if (IsSyncing && ReferenceEquals(initialDownload, download)) ApplyDownloadProgress(download, loginProgress);
        });
        void Changed() => progress.Report(download.State);
        download.Changed += Changed;
        try
        {
            await Task.Run(() => download.RunAsync(client, store, password, remember, fullRefresh));
            if (!ReadingDuringDownload)
                await ActivateAccountAsync(download.Account.Url, download.Account.Username, download.Year,
                    download.Account.ChildName, download.Account.SchoolName);
            ApplyDownloadProgress(download, loginProgress);
            if (ReadingDuringDownload) RefreshDownloadAttachments(download, all: true);
            await AttachmentAvailabilityReady;
            SetStatus(download.State.Status, true);
        }
        catch (ObkException error)
        {
            ApplyDownloadProgress(download, loginProgress);
            if (ReadingDuringDownload) RefreshDownloadAttachments(download, all: true);
            ReportError(download.State.Error is null ? error.Message : download.State.Status);
            throw;
        }
        finally { download.Changed -= Changed; IsSyncing = false; }
    }

    private void ApplyDownloadProgress(InitialDownload download, Action<InitialDownloadState> loginProgress)
    {
        DownloadState = download.State;
        loginProgress(download.State);
        if (IsSyncing && !IsExporting) SetStatus(download.State.Status, download.State.Phase == InitialDownloadPhase.Complete);
        if (!ReadingDuringDownload || refreshedAttachmentCount == download.State.CompletedAttachments) return;
        refreshedAttachmentCount = download.State.CompletedAttachments;
        RefreshDownloadAttachments(download);
    }

    public async Task ReadWhileDownloadingAsync()
    {
        if (initialDownload is not { } download || !download.State.MessagesReady || download.State.Error is not null)
            throw new ObkException(SharedStrings.DownloadWait);
        readingDuringDownload = true;
        Notify(nameof(ReadingDuringDownload));
        Notify(nameof(ShowDownloadStrip));
        Notify(nameof(DownloadActivity));
        UpdateActions();
        try
        {
            await ActivateAccountAsync(download.Account.Url, download.Account.Username, download.Year,
                download.Account.ChildName, download.Account.SchoolName);
            RefreshDownloadAttachments(download);
            await AttachmentAvailabilityReady;
            SetStatus(download.State.Status, download.State.Phase == InitialDownloadPhase.Complete);
        }
        catch (ObkException)
        {
            readingDuringDownload = false;
            Notify(nameof(ReadingDuringDownload));
            Notify(nameof(ShowDownloadStrip));
            UpdateActions();
            throw;
        }
    }

    /// <summary>
    /// Updates file availability while the sign-in download continues. Requests during a refresh are coalesced
    /// into one more pass, and only the open message's files are read until the final pass over the archive.
    /// </summary>
    private void RefreshDownloadAttachments(InitialDownload download, bool all = false)
    {
        attachmentRefreshAll |= all;
        if (!AttachmentAvailabilityReady.IsCompleted) { attachmentRefreshQueued = true; return; }
        AttachmentAvailabilityReady = RefreshDownloadAttachmentsAsync(download);
    }

    private void ClearInitialDownloadStatus()
    {
        initialDownload = null;
        readingDuringDownload = false;
        DownloadState = null;
        Notify(nameof(ReadingDuringDownload));
        Notify(nameof(ShowDownloadStrip));
        UpdateActions();
    }

    private async Task RefreshDownloadAttachmentsAsync(InitialDownload download)
    {
        do
        {
            attachmentRefreshQueued = false;
            var all = attachmentRefreshAll;
            attachmentRefreshAll = false;
            var child = archives.FirstOrDefault(item => Same(item.Login, download.Account));
            if (child is null) return;
            var rows = attachments.Where(row => Same(row.Account, child.Login)).ToArray();
            try
            {
                var info = await Task.Run(() => all ? ReadAttachmentInfo(child.Login.Url, child.Login.Username, child.Items) :
                    rows.Select(row => row.Attachment.Identifier).Distinct()
                        .ToDictionary(id => id, id => store.AttachmentInfo(child.Login.Url, child.Login.Username, id)));
                if (!ReferenceEquals(initialDownload, download) || !archives.Contains(child)) return;
                foreach (var entry in info) child.Info[entry.Key] = entry.Value;
                foreach (var row in rows) row.UpdateInfo(info.GetValueOrDefault(row.Attachment.Identifier));
                UpdateActions();
            }
            catch (ObkException error) { ReportError(error.Message); }
        } while (attachmentRefreshQueued);
    }

    /// <summary>
    /// Synchronizes the selected refresh scope. Accounts without a remembered password are reported, and the
    /// sign-in dialog opens for the current account or the first account that cannot refresh.
    /// </summary>
    public async Task RefreshAllAsync(bool automatic = false)
    {
        if (!CanChangeAccount) return;
        if (!automatic && selectedFolder?.Key == "marks") { await RefreshMarksAsync(); return; }
        IsSyncing = true;
        (bool Needed, Login? Account) prompt;
        try { prompt = await RefreshScopeAsync(automatic); }
        finally { IsSyncing = false; }
        if (prompt.Needed) openLogin(false, prompt.Account);
    }

    private async Task<(bool Needed, Login? Account)> RefreshScopeAsync(bool automatic)
    {
        ClearInitialDownloadStatus();
        var (known, remembered) = await Task.Run(() => (store.KnownLogins(), store.RememberedLogins()));
        if (known.Count == 0) return (!automatic, null);
        var targets = RefreshScope.Accounts(known, Account, showingAllChildren, Theme.AlwaysRefreshAllChildren);
        string Label(Login login) => login.ChildName ?? login.SchoolName ?? login.Username;
        var ready = new List<Login>();
        var missing = new List<Login>();
        foreach (var login in targets)
        {
            if (remembered.FirstOrDefault(r => r.Url == login.Url && r.Username == login.Username) is { Password: not null } saved)
                ready.Add(saved);
            else missing.Add(login);
        }
        var failures = new List<string>();
        var syncYear = SchoolYear.Current();
        for (var i = 0; i < ready.Count; i++)
        {
            var login = ready[i];
            var position = i + 1;
            string AccountStatus(string text) => ready.Count > 1
                ? Localized.Format(AppStrings.StatusAccountProgressNumbered, Label(login), position, ready.Count, text)
                : Localized.Format(AppStrings.StatusAccountProgress, Label(login), text);
            SetStatus(AccountStatus(AppStrings.StatusSigningIn));
            var progress = new Progress<string>(text => { if (IsSyncing) SetStatus(AccountStatus(text)); });
            try
            {
                using var client = ClientFactory(login.Url);
                var token = await Task.Run(() => client.SignInAsync(login.Username, login.Password!));
                await DownloadAsync(client, token, login.Url, login.Username, syncYear, false, progress);
            }
            catch (ObkException error) { failures.Add(Localized.Format(SharedStrings.ErrorInContext, Label(login), error.Message)); }
        }
        await LoadAsync(Account, syncYear);
        var refreshed = ready.Count - failures.Count;
        var summary = new List<string>();
        if (ready.Count > 0)
            summary.Add(Localized.Format(ready.Count == 1 ? AppStrings.RefreshSummaryOne : AppStrings.RefreshSummaryMany,
                refreshed, ready.Count));
        if (failures.Count > 0) summary.Add(Localized.Format(AppStrings.RefreshSummaryFailed, string.Join("; ", failures)));
        if (missing.Count > 0)
            summary.Add(Localized.Format(AppStrings.RefreshSummarySignInNeeded, string.Join(", ", missing.Select(Label))));
        SetStatus(string.Join(" ", summary), failures.Count == 0 && missing.Count == 0);
        // All children has no single current child, so it prompts only when nothing could refresh.
        var current = showingAllChildren ? null : Account;
        var prompt = missing.FirstOrDefault(m => Same(m, current)) ?? (ready.Count == 0 ? missing.FirstOrDefault() : null);
        return (!automatic && prompt is not null, prompt);
    }

    private async Task RefreshMarksAsync()
    {
        if (!CanChangeAccount) return;
        IsSyncing = true;
        try
        {
            var known = await Task.Run(store.KnownLogins);
            var targets = RefreshScope.Accounts(known, Account, showingAllChildren, Theme.AlwaysRefreshAllChildren);
            if (targets.Count == 0)
            {
                IsSyncing = false;
                openLogin(true, null);
                return;
            }
            var failures = new List<string>();
            var completed = 0;
            foreach (var login in targets)
            {
                var label = AccountRow.LabelFor(login);
                Marks.Status = Localized.Format(SharedStrings.MarksRefreshing, label);
                try
                {
                    var password = login.Password ?? (RequestMarksPassword is { } prompt ? await prompt(login) : null);
                    if (password is null)
                    {
                        failures.Add(Localized.Format(SharedStrings.ErrorInContext, label, SharedStrings.ErrorMissingCredentials));
                        continue;
                    }
                    using var client = new KomensClient(login.Url);
                    var token = await client.SignInAsync(login.Username, password);
                    await client.RefreshMarksAsync(token, store, login.Username);
                    completed++;
                }
                catch (ObkException error) { failures.Add(Localized.Format(SharedStrings.ErrorInContext, label, error.Message)); }
            }
            await Marks.LoadAsync(ScopeArchives().Select(child => child.Login).ToArray());
            Marks.Status = Localized.Format(SharedStrings.MarksRefreshSummary, completed, targets.Count) +
                (failures.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, failures) : "");
            SetStatus(Marks.Status, failures.Count == 0);
        }
        finally { IsSyncing = false; }
    }
}
