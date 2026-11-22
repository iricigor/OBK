using OBK.Core;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace OBK.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly LocalStore store;
    private Dictionary<string, List<Message>> folders = Folders.Empty();
    private Dictionary<string, AttachmentInfo?> attachmentInfo = [];
    private SchoolYear year = SchoolYear.Current();
    private string school = "Your school";
    private string? syncedAt;
    private Login? account;
    private bool syncing;
    private bool loading;
    private bool exporting;
    private bool goodStatus;
    private string status = "Sign in to download messages, or read your archive offline.";
    private FolderRow? selectedFolder;
    private MessageRow? selectedMessage;
    private readonly Dictionary<string, string> selectedMessageIds = [];
    private IReadOnlyList<FolderRow> navigation = [];
    private IReadOnlyList<MessageRow> messages = [];
    private IReadOnlyList<AttachmentRow> attachments = [];
    private IReadOnlyList<TextPart> body = [];
    private string subject = "Select a message";
    private string sender = "Your offline workspace";
    private string initials = "OB";
    private string metadata = "Viewing cached messages makes no server requests.";
    private bool expanded;
    private int folderVersion;
    private int messageVersion;

    public MainViewModel(LocalStore store, ThemeManager theme, Action<bool> openLogin,
        Func<AttachmentRow, string, Task> export, Func<IReadOnlyList<AttachmentRow>, Task> saveAll)
    {
        this.store = store;
        Theme = theme;
        SignInCommand = new RelayCommand(_ => openLogin(false), _ => CanChangeAccount);
        ToggleThemeCommand = new AsyncCommand(_ => Theme.ToggleAsync(), ReportError);
        SwitchSchoolCommand = new RelayCommand(_ => openLogin(true), _ => CanChangeAccount);
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
    public ICommand SignInCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SwitchSchoolCommand { get; }
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
            Notify(nameof(WindowTitle));
        }
    }
    public string AccountLabel => Account?.ChildName is { Length: > 0 } childName ? childName : School;
    public string WindowTitle => AccountLabel == "Your school" ? "OBK - Offline Komens" :
        $"OBK - Offline Komens - {AccountLabel}";
    public string YearLabel => $"School year {year.Label} | Sept 1 - Aug 31";
    public string FolderTitle => selectedFolder?.Title ?? "Inbox";
    public string FolderCaption => $"{folders[selectedFolder?.Key ?? "received"].Count} messages | School year {year.Label}";
    public IReadOnlyList<FolderRow> Navigation { get => navigation; private set => Set(ref navigation, value); }
    public IReadOnlyList<MessageRow> Messages { get => messages; private set => Set(ref messages, value); }
    public bool EmptyFolder => !Loading && messages.Count == 0;
    public string EmptyFolderText => $"No cached messages in {FolderTitle.ToLowerInvariant()}.\nSign in / Sync to check your school.";
    public bool IsSyncing { get => syncing; private set { if (Set(ref syncing, value)) UpdateActions(); } }
    public bool Loading { get => loading; private set { if (Set(ref loading, value)) { UpdateActions(); Notify(nameof(EmptyFolder)); } } }
    public bool IsExporting { get => exporting; set { if (Set(ref exporting, value)) UpdateActions(); } }
    public bool CanChangeAccount => !IsSyncing && !Loading && !IsExporting;
    public string Status { get => status; private set => Set(ref status, value); }
    public bool GoodStatus { get => goodStatus; private set => Set(ref goodStatus, value); }
    public string Subject { get => subject; private set => Set(ref subject, value); }
    public string Sender { get => sender; private set => Set(ref sender, value); }
    public string Initials { get => initials; private set => Set(ref initials, value); }
    public string Metadata { get => metadata; private set => Set(ref metadata, value); }
    public IReadOnlyList<TextPart> Body { get => body; private set => Set(ref body, value); }
    public bool HasAttachments => attachments.Count > 0;
    public bool MultipleAttachments => attachments.Count > 1;
    public IReadOnlyList<AttachmentRow> VisibleAttachments => attachments.Count > 4 && !expanded ? attachments.Take(3).ToArray() : attachments;
    public bool HasMoreAttachments => attachments.Count > 4;
    public string MoreAttachmentsLabel => expanded ? "Show fewer" : $"+{attachments.Count - 3} more";
    public bool AttachmentsExpanded
    {
        get => expanded;
        private set { if (Set(ref expanded, value)) { Notify(nameof(VisibleAttachments)); Notify(nameof(MoreAttachmentsLabel)); } }
    }

    public FolderRow? SelectedFolder
    {
        get => selectedFolder;
        set
        {
            if (!Set(ref selectedFolder, value) || value is null) return;
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

    private void UpdateActions() => CommandManager.InvalidateRequerySuggested();
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
        }
        catch (ObkException error) { ReportError(error.Message); }
        if (themeError is not null) ReportError(themeError);
    }

    public async Task ActivateAccountAsync(string url, string username, SchoolYear? selectedYear = null,
        string? childName = null, string? schoolName = null)
    {
        if (Loading || IsExporting) throw new ObkException("Wait for the current archive operation to finish.");
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
                selectedMessageIds.Clear();
            Account = new(normalized, username, ChildName: childName?.Trim() is { Length: > 0 } child ? child : null,
                SchoolName: schoolName?.Trim() is { Length: > 0 } schoolLabel ? schoolLabel : null);
            year = nextYear;
            folders = result.items;
            attachmentInfo = result.info;
            School = Account.SchoolName ?? result.archive?.SchoolName ?? "Your school";
            syncedAt = result.archive?.SyncedAt;
            Notify(nameof(YearLabel));
            RebuildNavigation();
            SetStatus(CacheStatus(), syncedAt is not null);
        }
        finally { Loading = false; }
    }

    private Dictionary<string, AttachmentInfo?> ReadAttachmentInfo(string url, string username, Dictionary<string, List<Message>> items) =>
        items.Values.SelectMany(m => m).SelectMany(m => m.Attachments).DistinctBy(a => a.Identifier)
            .ToDictionary(a => a.Identifier, a => store.AttachmentInfo(url, username, a.Identifier));

    private string CacheStatus() => syncedAt is null ?
        "No offline archive for this school year. Sign in to download messages." :
        $"Offline archive ready \u2022 Synced ({folders.Values.Sum(m => m.Count)} messages, {attachmentInfo.Values.Count(a => a is not null)} attachments) \u2022 Last synchronized: {DateTimeOffset.Parse(syncedAt).ToLocalTime():dd MMM yyyy HH:mm}";

    public void OfflineStatus() => SetStatus(syncedAt is null ? CacheStatus() : CacheStatus() + " \u2022 Offline", syncedAt is not null);

    private void RebuildNavigation()
    {
        var key = selectedFolder?.Key ?? "received";
        Navigation =
        [
            new("received", "Inbox", "\uE715", folders["received"].Count),
            new("sent", "Sent", "\uE724", folders["sent"].Count),
            new("noticeboard", "Noticeboard", "\uE789", folders["noticeboard"].Count)
        ];
        selectedFolder = Navigation.Single(f => f.Key == key);
        Notify(nameof(SelectedFolder));
        RefreshFolder();
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
        Notify(nameof(FolderCaption));
        Notify(nameof(EmptyFolderText));
        SelectedMessage = null;
        ClearMessage();
        Messages = [];
        Notify(nameof(EmptyFolder));
        try
        {
            var source = folders[key];
            selectedMessageIds.TryGetValue(key, out var rememberedId);
            var result = await Task.Run(() =>
            {
                var rows = source.Select(m => new MessageRow(m, key)).ToArray();
                var selected = rows.FirstOrDefault(r => r.Message.Id == rememberedId) ??
                    rows.MaxBy(r => r.Message.Date);
                return (rows, selected);
            });
            if (version != folderVersion) return;
            Messages = result.rows;
            SelectedMessage = result.selected;
            Notify(nameof(EmptyFolder));
        }
        catch (ObkException error) { if (version == folderVersion) ReportError(error.Message); }
    }

    private void ClearMessage()
    {
        ++messageVersion;
        Subject = "Select a message";
        Sender = "Your offline workspace";
        Initials = "OB";
        Metadata = "Viewing cached messages makes no server requests.";
        Body = [];
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
        var key = selectedFolder?.Key ?? "received";
        var info = attachmentInfo;
        Subject = row.Subject;
        Sender = OneLine(row.Message.SenderName) is { Length: > 0 } name ? name : "(Not supplied)";
        var words = Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Contains('.')).ToArray();
        Initials = string.IsNullOrWhiteSpace(row.Message.SenderName) || words.Length == 0 ? "?" :
            string.Concat(words.TakeLast(2).Select(w => w[0])).ToUpperInvariant();
        Metadata = $"Sent: {row.Sent} \u2022 Folder: {Folders.Names[key]}";
        if (key == "sent" && !string.IsNullOrEmpty(row.Message.RelevantName))
            Metadata += $"\nTo: {OneLine(row.Message.RelevantName)}";
        if (row.Message.CanConfirm == true && row.Message.Confirmed != true)
            Metadata += "\nConfirmation requested. OBK does not send confirmations.";
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
                return (parts, files);
            });
            if (version != messageVersion) return;
            Body = result.parts.Count == 0 ? [new("(No message content)", false, false, false, false)] : result.parts;
            SetAttachments(result.files);
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

    public async Task SynchronizeAsync(string url, string username, string password, bool remember, bool fullRefresh,
        string? childName, string? schoolName, IProgress<string> loginProgress)
    {
        if (!CanChangeAccount) throw new ObkException("Wait for the current operation to finish.");
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
            var result = await Task.Run(async () =>
            {
                var schoolName = await client.SchoolNameAsync(token);
                var cached = fullRefresh ? null : store.LoadArchive(url, username, syncYear);
                var fetched = await client.FetchYearAsync(token, syncYear, cached?.Folders, progress);
                await client.DownloadAttachmentsAsync(token, fetched, store, username, progress);
                var info = ReadAttachmentInfo(url, username, fetched);
                var stamp = store.SaveArchive(url, username, syncYear, schoolName, fetched);
                return (schoolName, fetched, stamp, info);
            });
            var schoolLabel = Account?.SchoolName ?? result.schoolName;
            Account = Account! with { SchoolName = schoolLabel };
            School = schoolLabel;
            folders = result.fetched;
            attachmentInfo = result.info;
            syncedAt = result.stamp;
            RebuildNavigation();
            SetStatus(CacheStatus(), true);
        }
        catch (ObkException error)
        {
            ReportError(error.Message + " Existing offline archive was not replaced.");
            throw;
        }
        finally { IsSyncing = false; }
    }
}
