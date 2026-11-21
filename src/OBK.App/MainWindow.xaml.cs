using Microsoft.Win32;
using OBK.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace OBK.App;

public partial class MainWindow : Window
{
    public LocalStore Store { get; }
    public Login? Account { get; private set; }
    public bool Busy { get; private set; }
    private readonly bool showLogin;
    private bool ready;
    private bool exporting;
    private SchoolYear year = SchoolYear.Current();
    private Dictionary<string, List<Message>> folders = Folders.Empty();
    private string folder = "received";
    private string school = "Your school";
    private string? syncedAt;
    private string startupError = "";

    public MainWindow(LocalStore store, bool showLogin = true)
    {
        Store = store;
        this.showLogin = showLogin;
        InitializeComponent();
        ready = true;
        ShowFolder();
        Status.Text = "Ready. Sign in to synchronize the current school year.";
        try
        {
            var last = store.LastAccount();
            if (last is not null) ActivateAccount(last.Url, last.Username);
        }
        catch (ObkException error) { startupError = error.Message; Status.Text = startupError; }
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (showLogin) OpenLoginDialog(startupError);
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (Busy || exporting)
        {
            e.Cancel = true;
            Status.Text = "Wait for synchronization or attachment export to finish before closing OBK.";
        }
    }

    private void OpenLogin(object sender, RoutedEventArgs e) => OpenLoginDialog();
    private void OpenLoginDialog(string error = "")
    {
        if (Busy || exporting) return;
        Login? initial = Account;
        var remembered = false;
        try
        {
            var saved = Store.RememberedLogin();
            if (saved is not null) { initial = saved; remembered = true; }
            else initial ??= Store.LastAccount();
        }
        catch (ObkException problem) { error = problem.Message; }
        new LoginWindow(this, initial, remembered, error).ShowDialog();
    }

    private void ForgetCredentials(object sender, RoutedEventArgs e)
    {
        try { Store.ForgetCredentials(); Status.Text = "Remembered credentials removed. Offline messages are unchanged."; }
        catch (ObkException error) { Status.Text = error.Message; }
    }

    public void ActivateAccount(string url, string username)
    {
        Account = new(KomensClient.NormalizeUrl(url), username);
        school = "Your school";
        folders = Folders.Empty();
        syncedAt = null;
        ShowFolder();
        var archive = Store.LoadArchive(url, username, year);
        if (archive is not null)
        {
            school = archive.SchoolName;
            folders = archive.Folders;
            syncedAt = archive.SyncedAt;
        }
        ShowFolder();
        Status.Text = CacheStatus();
    }

    private string CacheStatus() => syncedAt is not null ?
        $"Offline archive | Last synchronized {DateTimeOffset.Parse(syncedAt).ToLocalTime():dd MMM yyyy HH:mm} | No server modification requests." :
        "No offline archive for this account and school year. Sign in to download messages.";

    public void OfflineStatus() => Status.Text = "Offline reading. No network requests were made.";

    internal void VerifyOfflineReader()
    {
        Messages.SelectedIndex = 0;
        if (Subject.Text != "Installation check" ||
            !new TextRange(ContentReader.Document.ContentStart, ContentReader.Document.ContentEnd).Text.Contains("Offline content") ||
            OpenAttachmentButton.IsEnabled)
            throw new ObkException("The offline reader did not render its synthetic message correctly.");
    }

    private void FolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ready && e.NewValue is TreeViewItem { Tag: string key } && Folders.Names.ContainsKey(key))
        {
            folder = key;
            ShowFolder();
        }
    }

    public static string OneLine(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
    private void ShowFolder()
    {
        if (!ready) return;
        YearLabel.Text = $"School year {year.Label} | September 1 - August 31";
        SchoolNode.Header = school;
        InboxNode.Header = $"Inbox ({folders["received"].Count})";
        SentNode.Header = $"Sent ({folders["sent"].Count})";
        NoticeboardNode.Header = $"Noticeboard ({folders["noticeboard"].Count})";
        ListTitle.Text = Folders.Names[folder];
        var list = folders[folder];
        Messages.ItemsSource = list.Select(message => new MessageRow(message, folder)).ToList();
        ListCaption.Text = list.Count != 0 ? $"{list.Count} messages | School year {year.Label}" :
            $"No cached messages in {Folders.Names[folder].ToLowerInvariant()} for {year.Label}. Synchronize to check the school.";
        ClearMessage();
    }

    private void ClearMessage()
    {
        Subject.Text = "Select a message";
        Metadata.Text = "Viewing cached messages makes no server requests.";
        ContentReader.Document = new FlowDocument();
        AttachmentList.ItemsSource = null;
        AttachmentPanel.Visibility = Visibility.Collapsed;
    }

    private void MessageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (Messages.SelectedItem is not MessageRow row) { ClearMessage(); return; }
        var message = row.Message;
        Subject.Text = row.Subject;
        Metadata.Text = $"From: {(OneLine(message.SenderName) is { Length: > 0 } name ? name : "(Not supplied)")}\n" +
            $"Sent: {message.Date:dd MMM yyyy HH:mm} | Folder: {Folders.Names[folder]}";
        if (folder == "sent" && !string.IsNullOrEmpty(message.RelevantName))
            Metadata.Text += $"\nTo: {OneLine(message.RelevantName)}";
        if (message.CanConfirm == true && message.Confirmed != true)
            Metadata.Text += "\nConfirmation requested. OBK does not send confirmations.";
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 22 };
        var parts = MessageRenderer.Render(message.Text);
        foreach (var part in parts)
        {
            var run = new Run(part.Text)
            {
                FontWeight = part.Bold || part.Heading ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = part.Italic ? FontStyles.Italic : FontStyles.Normal,
                FontSize = part.Heading ? 18 : 15
            };
            if (part.Underline) run.TextDecorations = TextDecorations.Underline;
            paragraph.Inlines.Add(run);
        }
        if (parts.Count == 0) paragraph.Inlines.Add(new Run("(No message content)"));
        ContentReader.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
        ContentReader.ScrollToHome();
        try
        {
            foreach (var attachment in message.Attachments) _ = attachment.SafeName;
            AttachmentList.ItemsSource = message.Attachments;
            AttachmentPanel.Visibility = message.Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            AttachmentTitle.Text = $"Attachments ({message.Attachments.Count})";
            AttachmentList.SelectedIndex = 0;
        }
        catch (ObkException error) { AttachmentPanel.Visibility = Visibility.Collapsed; Status.Text = error.Message; }
    }

    private void AttachmentChanged(object sender, SelectionChangedEventArgs e) => UpdateAttachment();
    private void UpdateAttachment()
    {
        if (!ready) return;
        OpenAttachmentButton.IsEnabled = SaveAttachmentButton.IsEnabled = false;
        if (Account is null || AttachmentList.SelectedItem is not Attachment attachment) return;
        try
        {
            var info = Store.AttachmentInfo(Account.Url, Account.Username, attachment.Identifier);
            if (info is null) { AttachmentStatus.Text = "Not downloaded. Sign in / Sync to cache attachments."; return; }
            AttachmentStatus.Text = $"Ready offline | {info.Size:N0} bytes";
            SaveAttachmentButton.IsEnabled = !exporting;
            OpenAttachmentButton.IsEnabled = !exporting && FileNames.CanOpen(info.Name);
            if (!FileNames.CanOpen(info.Name))
                AttachmentStatus.Text += " | This file type can be saved, but not opened directly.";
        }
        catch (ObkException error) { AttachmentStatus.Text = error.Message; }
    }

    private async void OpenAttachment(object sender, RoutedEventArgs e) => await ExportSelectedAsync(true);
    private async void SaveAttachment(object sender, RoutedEventArgs e) => await ExportSelectedAsync(false);
    private async Task ExportSelectedAsync(bool open)
    {
        if (exporting || Account is not { } account || AttachmentList.SelectedItem is not Attachment attachment) return;
        try
        {
            var id = attachment.Identifier;
            var info = Store.AttachmentInfo(account.Url, account.Username, id) ??
                throw new ObkException("Sign in and sync to download this attachment first.");
            string destination;
            if (open)
            {
                if (!FileNames.CanOpen(info.Name)) throw new ObkException("This file type cannot be opened directly. Use Save a copy.");
                if (MessageBox.Show(this,
                    "Only open files you trust. Documents can contain active content.\n\nOBK will create a decrypted copy in its local 'opened' folder and launch your default Windows viewer. That copy remains on disk.",
                    "Open attachment?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                var directory = Path.Combine(Path.GetDirectoryName(Store.Path)!, "opened", "attachment-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                destination = Path.Combine(directory, info.Name);
            }
            else
            {
                var dialog = new SaveFileDialog { Title = "Save a decrypted attachment copy", FileName = info.Name, DefaultExt = Path.GetExtension(info.Name) };
                if (dialog.ShowDialog(this) != true) return;
                destination = dialog.FileName;
            }
            exporting = true;
            SyncButton.IsEnabled = false;
            UpdateAttachment();
            Status.Text = "Preparing the attachment copy...";
            var path = await Task.Run(() => Store.ExportAttachment(account.Url, account.Username, id, destination));
            if (open)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    Status.Text = $"Opened attachment copy: {Path.GetFileName(path)}";
                }
                catch (Win32Exception)
                {
                    Status.Text = "The copy was created, but Windows could not open it. Install a default viewer or use Save a copy.";
                }
            }
            else Status.Text = $"Saved attachment copy: {Path.GetFileName(path)}";
        }
        catch (ObkException error) { Status.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Cannot create an attachment copy folder. Check disk access.";
        }
        finally { exporting = false; SyncButton.IsEnabled = !Busy; UpdateAttachment(); }
    }

    public async Task SynchronizeAsync(LoginWindow login, string url, string username, string password, bool remember, bool fullRefresh)
    {
        Busy = true;
        SyncButton.IsEnabled = ForgetButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        var syncYear = SchoolYear.Current();
        var progress = new Progress<string>(text =>
        {
            if (Busy) { Status.Text = text; login.StatusText = text; }
        });
        try
        {
            var result = await Task.Run(async () =>
            {
                using var client = new KomensClient(url);
                var token = await client.SignInAsync(username, password);
                Store.SaveLogin(url, username, password, remember);
                await Dispatcher.InvokeAsync(() => { year = syncYear; ActivateAccount(url, username); });
                var schoolName = await client.SchoolNameAsync(token);
                var cached = fullRefresh ? null : Store.LoadArchive(url, username, syncYear);
                var fetched = await client.FetchYearAsync(token, syncYear, cached?.Folders, progress);
                var count = await client.DownloadAttachmentsAsync(token, fetched, Store, username, progress);
                var stamp = Store.SaveArchive(url, username, syncYear, schoolName, fetched);
                return (schoolName, fetched, count, stamp);
            });
            school = result.schoolName;
            folders = result.fetched;
            syncedAt = result.stamp;
            ShowFolder();
            Status.Text = $"Synchronized {folders.Values.Sum(m => m.Count)} messages and {result.count} attachments. {CacheStatus()}";
            Busy = false;
            login.Complete();
        }
        catch (ObkException error)
        {
            Status.Text = error.Message + " Existing offline archive was not replaced.";
            login.StatusText = error.Message + " Existing offline messages remain available.";
        }
        finally
        {
            Busy = false;
            SyncButton.IsEnabled = ForgetButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            login.SetBusy(false);
        }
    }
}

public sealed class MessageRow(Message message, string folder)
{
    public Message Message { get; } = message;
    public bool Unread => folder != "sent" && Message.Read == false;
    public string State => Unread ? "Unread" : "";
    public string Subject => MainWindow.OneLine(Message.Title) is { Length: > 0 } title ? title : "(No subject)";
    public string Person => MainWindow.OneLine(folder == "sent" ? Message.RelevantName ?? Message.SenderName : Message.SenderName);
    public string Sent => Message.Date.ToString("dd MMM yyyy HH:mm");
    public string Files => Message.Attachments.Count == 0 ? "" : Message.Attachments.Count.ToString();
}
