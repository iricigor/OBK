using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;

namespace OBK.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly LocalStore store;
    private readonly Action<bool, Login?> openLogin;
    private Dictionary<string, List<Message>> folders = Folders.Empty();
    private Dictionary<string, AttachmentInfo?> attachmentInfo = [];
    private SchoolYear year = SchoolYear.Current();
    private string school = AppStrings.MainDefaultSchool;
    private string? syncedAt;
    private Login? account;
    private bool syncing;
    private bool loading;
    private bool exporting;
    private bool goodStatus;
    private string status = AppStrings.StatusWelcome;
    private FolderRow? selectedFolder;
    private MessageRow? selectedMessage;
    private readonly Dictionary<string, string> selectedMessageIds = [];
    private IReadOnlyList<FolderRow> navigation = [];
    private IReadOnlyList<AccountRow> accounts = [];
    private IReadOnlyList<MessageRow> folderRows = [];
    private IReadOnlyList<MessageRow> messages = [];
    private ICollectionView? messagesView;
    private IReadOnlyList<AttachmentRow> attachments = [];
    private IReadOnlyList<TextPart> body = [];
    private IReadOnlyList<RelatedRow> related = [];
    private string subject = AppStrings.ReaderNoSelection;
    private string sender = AppStrings.ReaderNoSelectionSender;
    private string initials = "OB";
    private string metadata = AppStrings.ReaderNoSelectionDetail;
    private string searchText = "";
    private MessageSort sort = MessageSort.Date;
    private MessageGrouping grouping = MessageGrouping.None;
    private bool selectedIsExcuse;
    private bool navigationExpanded = true;
    private bool settingsOpen;
    private bool expanded;
    private int folderVersion;
    private int listVersion;
    private int messageVersion;

    public MainViewModel(LocalStore store, ThemeManager theme, Action<bool, Login?> openLogin,
        Func<AttachmentRow, string, Task> export, Func<IReadOnlyList<AttachmentRow>, Task> saveAll)
    {
        this.store = store;
        this.openLogin = openLogin;
        Theme = theme;
        RefreshCommand = new AsyncCommand(_ => RefreshAllAsync(), ReportError, _ => CanChangeAccount);
        ToggleThemeCommand = new AsyncCommand(_ => Theme.ToggleAsync(), ReportError);
        SetThemeCommand = new AsyncCommand(p => Theme.SetAsync(p as string ?? ""), ReportError);
        SetReadingSizeCommand = new AsyncCommand(p => Theme.SetReadingSizeAsync(p as string ?? ""), ReportError);
        SetListDensityCommand = new AsyncCommand(p => Theme.SetListDensityAsync(p as string ?? ""), ReportError);
        OpenSettingsCommand = new RelayCommand(_ => SettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(_ => SettingsOpen = false);
        SwitchSchoolCommand = new RelayCommand(_ => openLogin(true, null), _ => CanChangeAccount);
        SelectAccountCommand = new AsyncCommand(p =>
            {
                SettingsOpen = false;
                return p is AccountRow { IsCurrent: false } row ? SelectAccountAsync(row) : Task.CompletedTask;
            }, ReportError, p => CanChangeAccount && p is AccountRow);
        OpenRelatedCommand = new RelayCommand(p => { if (p is RelatedRow row) OpenRelated(row.Item); });
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
        ToggleNavigationCommand = new RelayCommand(_ => NavigationExpanded = !NavigationExpanded);
        OpenAttachmentCommand = ExportCommand("open", export);
        SaveAttachmentCommand = ExportCommand("save", export);
        OpenFolderCommand = ExportCommand("folder", export);
        SaveAllCommand = new AsyncCommand(_ => saveAll(attachments), ReportError,
            _ => CanChangeAccount && attachments.Count > 1 && attachments.All(a => a.Cached));
        ExpandAttachmentsCommand = new RelayCommand(_ => AttachmentsExpanded = !AttachmentsExpanded);
        RebuildNavigation();
    }

    private AsyncCommand ExportCommand(string action, Func<AttachmentRow, string, Task> export) =>
        new(p => p is AttachmentRow row ? export(row, action) : Task.CompletedTask, ReportError,
            p => CanChangeAccount && p is AttachmentRow { Cached: true });

    public ThemeManager Theme { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SetThemeCommand { get; }
    public ICommand SetReadingSizeCommand { get; }
    public ICommand SetListDensityCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public bool SettingsOpen
    {
        get => settingsOpen;
        set { if (Set(ref settingsOpen, value)) Notify(nameof(ShowMessages)); }
    }
    public bool ShowMessages => !settingsOpen;
    public ICommand SwitchSchoolCommand { get; }
    public ICommand SelectAccountCommand { get; }
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
    public string WindowTitle => AccountLabel == AppStrings.MainDefaultSchool ? AppStrings.AppTitle :
        Localized.Format(AppStrings.AppTitleWithAccount, AccountLabel);
    public string FolderTitle => selectedFolder?.Title ?? SharedStrings.FolderInbox;
    public string FolderCaption
    {
        get
        {
            var total = FolderMessages(selectedFolder?.Key ?? "received").Count;
            return SearchActive ? Localized.Format(AppStrings.MainFolderSearchCount, messages.Count, total) :
                Localized.Format(AppStrings.MainFolderCount, total);
        }
    }
    public IReadOnlyList<FolderRow> Navigation { get => navigation; private set => Set(ref navigation, value); }
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
    public bool CanChangeAccount => !IsSyncing && !Loading && !IsExporting;
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
    public IReadOnlyList<DisplayOption<MessageGrouping>> GroupOptions { get; } = Enum.GetValues<MessageGrouping>()
        .Select(value => new DisplayOption<MessageGrouping>(value, value switch
        {
            MessageGrouping.None => AppStrings.MainGroupOptionNone,
            MessageGrouping.Date => AppStrings.MainGroupOptionDate,
            MessageGrouping.Sender => AppStrings.MainGroupOptionSender,
            MessageGrouping.Title => AppStrings.MainGroupOptionTitle,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        })).ToArray();
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
    public MessageGrouping Grouping { get => grouping; set { if (Set(ref grouping, value)) ApplyList(); } }

    public FolderRow? SelectedFolder
    {
        get => selectedFolder;
        set
        {
            if (!Set(ref selectedFolder, value) || value is null) return;
            SettingsOpen = false;
            RefreshFolder();
        }
    }
    public MessageRow? SelectedMessage
    {
        get => selectedMessage;
        set
        {
            if (!Set(ref selectedMessage, value)) return;
            if (value is not null && selectedFolder is { } folder)
                selectedMessageIds[folder.Key] = value.Message.Id;
            RefreshMessage();
        }
    }

    private void UpdateActions()
    {
        Notify(nameof(CanChangeAccount));
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
            if (last is not null)
                await ActivateAccountAsync(last.Url, last.Username, childName: last.ChildName, schoolName: last.SchoolName);
            else await ReloadAccountsAsync();
        }
        catch (ObkException error) { ReportError(error.Message); }
        if (themeError is not null) ReportError(themeError);
    }

    public async Task ActivateAccountAsync(string url, string username, SchoolYear? selectedYear = null,
        string? childName = null, string? schoolName = null)
    {
        if (Loading || IsExporting) throw new ObkException(AppStrings.StatusWaitForArchive);
        Loading = true;
        try
        {
            var nextYear = selectedYear ?? year;
            var normalized = KomensClient.NormalizeUrl(url);
            var result = await Task.Run(() =>
            {
                var archive = store.LoadArchive(normalized, username, nextYear);
                var items = archive?.Folders ?? Folders.Empty();
                return (archive, items, info: ReadAttachmentInfo(normalized, username, items));
            });
            if (Account is not { } previous || previous.Url != normalized ||
                previous.Username != username || year != nextYear)
            {
                selectedMessageIds.Clear();
                searchText = "";
                Notify(nameof(SearchText));
                Notify(nameof(SearchActive));
            }
            Account = new(normalized, username, ChildName: childName?.Trim() is { Length: > 0 } child ? child : null,
                SchoolName: schoolName?.Trim() is { Length: > 0 } schoolLabel ? schoolLabel : null);
            year = nextYear;
            folders = result.items;
            attachmentInfo = result.info;
            School = Account.SchoolName ?? result.archive?.SchoolName ?? AppStrings.MainDefaultSchool;
            syncedAt = result.archive?.SyncedAt;
            RebuildNavigation();
            SetStatus(CacheStatus(), syncedAt is not null);
        }
        finally { Loading = false; }
        await ReloadAccountsAsync();
    }

    private async Task ReloadAccountsAsync()
    {
        try
        {
            var known = await Task.Run(store.KnownLogins);
            var comparer = StringComparer.Create(LanguageSettings.Culture, true);
            Accounts = known.Select(login => new AccountRow(login with { Password = null },
                    Account is { } current && current.Url == login.Url && current.Username == login.Username,
                    login.Password is not null))
                .OrderBy(row => row.Label, comparer).ThenBy(row => row.Login.Url, StringComparer.Ordinal)
                .ThenBy(row => row.Login.Username, StringComparer.Ordinal).ToArray();
        }
        catch (ObkException error) { ReportError(error.Message); }
    }

    private async Task SelectAccountAsync(AccountRow row)
    {
        var login = row.Login;
        await Task.Run(() => store.SaveAccount(login.Url, login.Username, login.ChildName, login.SchoolName));
        await ActivateAccountAsync(login.Url, login.Username, SchoolYear.Current(), login.ChildName, login.SchoolName);
    }

    public async Task RenameAccountAsync(AccountRow row, string? childName)
    {
        if (!CanChangeAccount) throw new ObkException(AppStrings.StatusWaitForOperation);
        var login = row.Login;
        var label = childName?.Trim() is { Length: > 0 } name ? name : null;
        await Task.Run(() => store.RenameAccount(login.Url, login.Username, label));
        if (Account is { } current && current.Url == login.Url && current.Username == login.Username)
            Account = current with { ChildName = label };
        await ReloadAccountsAsync();
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

    private string CacheStatus() => syncedAt is null ?
        AppStrings.StatusNoArchive :
        Localized.Format(AppStrings.StatusArchiveReady, folders.Values.Sum(m => m.Count),
            attachmentInfo.Values.Count(a => a is not null),
            DateTimeOffset.Parse(syncedAt).ToLocalTime().ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture));

    public sealed record ReaderState(Login? Account, SchoolYear Year, string Folder, string Search,
        MessageSort Sort, MessageGrouping Grouping, Dictionary<string, string> MessageIds,
        bool NavigationExpanded, bool SettingsOpen, bool AttachmentsExpanded);

    public ReaderState CaptureState() => new(Account, year, SelectedFolder?.Key ?? "received", SearchText,
        Sort, Grouping, new(selectedMessageIds), NavigationExpanded, SettingsOpen, AttachmentsExpanded);

    public async Task RestoreStateAsync(ReaderState state)
    {
        if (state.Account is { } login)
            await ActivateAccountAsync(login.Url, login.Username, state.Year, login.ChildName, login.SchoolName);
        else await ReloadAccountsAsync();
        await FolderReady;
        selectedMessageIds.Clear();
        foreach (var (key, id) in state.MessageIds) selectedMessageIds[key] = id;
        searchText = state.Search;
        sort = state.Sort;
        grouping = state.Grouping;
        selectedFolder = Navigation.Single(folder => folder.Key == state.Folder);
        Notify(nameof(SearchText));
        Notify(nameof(SearchActive));
        Notify(nameof(Sort));
        Notify(nameof(Grouping));
        Notify(nameof(SelectedFolder));
        FolderReady = RefreshFolderAsync();
        await FolderReady;
        await MessageReady;
        NavigationExpanded = state.NavigationExpanded;
        SettingsOpen = state.SettingsOpen;
        AttachmentsExpanded = state.AttachmentsExpanded;
    }

    public void OfflineStatus() => SetStatus(syncedAt is null ? CacheStatus() :
        Localized.Format(AppStrings.StatusOffline, CacheStatus()), syncedAt is not null);

    private void RebuildNavigation()
    {
        var key = selectedFolder?.Key ?? "received";
        Navigation =
        [
            new("noticeboard", Folders.Title("noticeboard"), "\uE789", folders["noticeboard"].Count),
            new("received", Folders.Title("received"), "\uE715", folders["received"].Count),
            new("sent", Folders.Title("sent"), "\uE724", FolderMessages("sent").Count),
            new("excuses", SharedStrings.FolderExcuses, "\uE8A5", FolderMessages("excuses").Count)
        ];
        selectedFolder = Navigation.Single(f => f.Key == key);
        Notify(nameof(SelectedFolder));
        RefreshFolder();
    }

    /// <summary>Excuses are a view over Sent, split by the API message type; storage keeps the server folders.</summary>
    private List<Message> FolderMessages(string key) => key switch
    {
        "excuses" => folders["sent"].Where(MessageInsights.IsExcuse).ToList(),
        "sent" => folders["sent"].Where(m => !MessageInsights.IsExcuse(m)).ToList(),
        _ => folders[key]
    };

    private void OpenRelated(MessageRef reference)
    {
        var item = reference.Folder == "sent" && MessageInsights.IsExcuse(reference.Message) ? reference with { Folder = "excuses" } : reference;
        selectedMessageIds[item.Folder] = item.Message.Id;
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
        SelectedMessage = null;
        ClearMessage();
        folderRows = [];
        Messages = [];
        MessagesView = null;
        Notify(nameof(FolderCaption));
        Notify(nameof(EmptyFolderText));
        Notify(nameof(EmptyFolder));
        try
        {
            var source = FolderMessages(key);
            var rowFolder = key == "excuses" ? "sent" : key;
            var rows = await Task.Run(() => source.Select(m => new MessageRow(m, rowFolder)).ToArray());
            if (version != folderVersion) return;
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
        var arranged = await Task.Run(() =>
        {
            var matching = rows.Where(r => MessageInsights.Matches(r.SearchText, query));
            return MessageInsights.Arrange(matching, r => r.Message, r => r.Person, sortBy, groupBy, DateTime.Today);
        });
        if (version != folderVersion || listId != listVersion) return;
        for (var index = 0; index < arranged.Count; index++)
        {
            var (row, group) = arranged[index];
            row.Group = group;
            row.IsAlternate = index % 2 == 1;
        }
        var list = arranged.Select(a => a.Item).ToList();
        var view = new ListCollectionView(list);
        if (groupBy != MessageGrouping.None) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MessageRow.Group)));
        var previous = selectedMessage;
        selectedMessageIds.TryGetValue(key, out var rememberedId);
        Messages = list;
        MessagesView = view;
        var selected = list.FirstOrDefault(r => ReferenceEquals(r, previous)) ??
            list.FirstOrDefault(r => r.Message.Id == rememberedId) ??
            (query.Trim().Length > 0 ? list.FirstOrDefault() : list.MaxBy(r => r.Message.Date));
        if (!ReferenceEquals(selected, selectedMessage)) SelectedMessage = selected;
        else Notify(nameof(SelectedMessage));
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
        var info = attachmentInfo;
        var all = folders;
        Subject = row.Subject;
        Sender = OneLine(row.Message.SenderName) is { Length: > 0 } name ? name : AppStrings.ReaderNotSupplied;
        var words = Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Contains('.')).ToArray();
        Initials = string.IsNullOrWhiteSpace(row.Message.SenderName) || words.Length == 0 ? "?" :
            string.Concat(words.TakeLast(2).Select(w => w[0])).ToUpperInvariant();
        SelectedIsExcuse = row.IsExcuse;
        Metadata = Localized.Format(AppStrings.ReaderMetadata, row.Sent, FolderTitle);
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
                    return new AttachmentRow(this, a, info.GetValueOrDefault(a.Identifier));
                }).ToArray();
                var related = MessageInsights.FindRelated(new(row.Message, key),
                    all.SelectMany(f => f.Value.Select(m => new MessageRef(m, f.Key))));
                return (parts, files, related);
            });
            if (version != messageVersion) return;
            Body = result.parts.Count == 0 ? [new(AppStrings.ReaderNoContent, false, false, false, false)] : result.parts;
            SetAttachments(result.files);
            Related = RelatedRow.Build(this, result.related);
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

    private async Task<(string SchoolName, Dictionary<string, List<Message>> Folders, string Stamp,
        Dictionary<string, AttachmentInfo?> Info)> DownloadAsync(KomensClient client, string token, string url,
        string username, SchoolYear syncYear, bool fullRefresh, IProgress<string> progress) =>
        await Task.Run(async () =>
        {
            var schoolName = await client.SchoolNameAsync(token);
            var cached = fullRefresh ? null : store.LoadArchive(url, username, syncYear);
            var fetched = await client.FetchYearAsync(token, syncYear, cached?.Folders, progress);
            await client.DownloadAttachmentsAsync(token, fetched, store, username, progress);
            var info = ReadAttachmentInfo(url, username, fetched);
            var stamp = store.SaveArchive(url, username, syncYear, schoolName, fetched);
            return (schoolName, fetched, stamp, info);
        });

    public async Task SynchronizeAsync(string url, string username, string password, bool remember, bool fullRefresh,
        string? childName, string? schoolName, IProgress<string> loginProgress)
    {
        if (!CanChangeAccount) throw new ObkException(AppStrings.StatusWaitForOperation);
        IsSyncing = true;
        var syncYear = SchoolYear.Current();
        var progress = new Progress<string>(text =>
        {
            if (IsSyncing) { SetStatus(text); loginProgress.Report(text); }
        });
        try
        {
            using var client = new KomensClient(url);
            var token = await Task.Run(async () =>
            {
                var result = await client.SignInAsync(username, password);
                store.SaveLogin(url, username, password, remember, childName, schoolName);
                return result;
            });
            await ActivateAccountAsync(url, username, syncYear, childName, schoolName);
            var result = await DownloadAsync(client, token, url, username, syncYear, fullRefresh, progress);
            var schoolLabel = Account?.SchoolName ?? result.SchoolName;
            Account = Account! with { SchoolName = schoolLabel };
            School = schoolLabel;
            folders = result.Folders;
            attachmentInfo = result.Info;
            syncedAt = result.Stamp;
            RebuildNavigation();
            SetStatus(CacheStatus(), true);
        }
        catch (ObkException error)
        {
            ReportError(Localized.Format(AppStrings.ErrorSyncArchiveKept, error.Message));
            throw;
        }
        finally { IsSyncing = false; }
        await ReloadAccountsAsync();
    }

    /// <summary>
    /// Synchronizes every known account that has a remembered password. Accounts without one are reported;
    /// the sign-in dialog opens for the current account, or for the first one if nothing could refresh.
    /// </summary>
    public async Task RefreshAllAsync()
    {
        if (!CanChangeAccount) return;
        var (known, remembered) = await Task.Run(() => (store.KnownLogins(), store.RememberedLogins()));
        if (known.Count == 0) { openLogin(false, null); return; }
        string Label(Login login) => login.ChildName ?? login.SchoolName ?? login.Username;
        var ready = new List<Login>();
        var missing = new List<Login>();
        foreach (var login in known)
        {
            if (remembered.FirstOrDefault(r => r.Url == login.Url && r.Username == login.Username) is { Password: not null } saved)
                ready.Add(saved);
            else missing.Add(login);
        }
        var failures = new List<string>();
        var syncYear = SchoolYear.Current();
        IsSyncing = true;
        try
        {
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
                    using var client = new KomensClient(login.Url);
                    var token = await Task.Run(() => client.SignInAsync(login.Username, login.Password!));
                    await DownloadAsync(client, token, login.Url, login.Username, syncYear, false, progress);
                }
                catch (ObkException error) { failures.Add(Localized.Format(SharedStrings.ErrorInContext, Label(login), error.Message)); }
            }
        }
        finally { IsSyncing = false; }
        if (Account is { } current)
            await ActivateAccountAsync(current.Url, current.Username, syncYear, current.ChildName, current.SchoolName);
        else await ReloadAccountsAsync();
        var refreshed = ready.Count - failures.Count;
        var summary = new List<string>();
        if (ready.Count > 0)
            summary.Add(Localized.Format(ready.Count == 1 ? AppStrings.RefreshSummaryOne : AppStrings.RefreshSummaryMany,
                refreshed, ready.Count));
        if (failures.Count > 0) summary.Add(Localized.Format(AppStrings.RefreshSummaryFailed, string.Join("; ", failures)));
        if (missing.Count > 0)
            summary.Add(Localized.Format(AppStrings.RefreshSummarySignInNeeded, string.Join(", ", missing.Select(Label))));
        SetStatus(string.Join(" ", summary), failures.Count == 0 && missing.Count == 0);
        var prompt = missing.FirstOrDefault(m => Account is { } a && a.Url == m.Url && a.Username == m.Username) ??
            (ready.Count == 0 ? missing.FirstOrDefault() : null);
        if (prompt is not null) openLogin(false, prompt);
    }
}
