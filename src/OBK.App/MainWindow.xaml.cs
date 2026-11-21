using Microsoft.Win32;
using OBK.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OBK.App;

public partial class MainWindow : Window
{
    public LocalStore Store { get; }
    public MainViewModel ViewModel { get; }
    public Login? Account => ViewModel.Account;
    public bool Busy => ViewModel.IsSyncing || ViewModel.Loading;
    public ICommand MinimizeCommand { get; }
    public ICommand MaximizeCommand { get; }
    public ICommand CloseCommand { get; }
    private readonly bool showLogin;
    private Task? initialization;
    private Point dragOrigin;
    private bool compact;
    private bool openingLogin;

    public MainWindow(LocalStore store, bool showLogin = true)
    {
        Store = store;
        this.showLogin = showLogin;
        MinimizeCommand = new RelayCommand(_ => SystemCommands.MinimizeWindow(this));
        MaximizeCommand = new RelayCommand(_ =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        });
        CloseCommand = new RelayCommand(_ => Close());
        var theme = new ThemeManager(Path.Combine(Path.GetDirectoryName(store.Path)!, "appearance.json"));
        ViewModel = new MainViewModel(store, theme, OpenLoginDialog, ExportAsync, SaveAllAsync);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedMessage)) ReadingScroll.ScrollToTop();
        };
    }

    public Task InitializeAsync() => initialization ??= ViewModel.InitializeAsync();
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeAsync();
        if (showLogin) OpenLoginDialog();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (Busy || ViewModel.IsExporting)
        {
            e.Cancel = true;
            ViewModel.ReportError("Wait for synchronization or attachment export to finish before closing OBK.");
        }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NavigationColumn is null) return;
        var next = ActualWidth < 1120;
        if (next != compact)
        {
            compact = next;
            NavigationColumn.Width = new GridLength(next ? 200 : 210);
            MessageColumn.Width = new GridLength(next ? 260 : 360);
        }
        YearLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        YearBadge.ToolTip = ViewModel.YearLabel;
        var maxListWidth = ActualWidth - 48 - NavigationColumn.ActualWidth - 300;
        if (MessageColumn.ActualWidth > maxListWidth)
            MessageColumn.Width = new GridLength(Math.Max(240, maxListWidth));
        WindowRoot.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private async void OpenLoginDialog()
    {
        if (openingLogin || !ViewModel.CanChangeAccount) return;
        openingLogin = true;
        try
        {
            Login? initial = Account;
            var remembered = false;
            var error = "";
            try
            {
                var saved = await Task.Run(Store.RememberedLogin);
                if (saved is not null) { initial = saved; remembered = true; }
                else initial ??= await Task.Run(Store.LastAccount);
            }
            catch (ObkException problem) { error = problem.Message; }
            new LoginWindow(this, initial, remembered, error).ShowDialog();
        }
        finally { openingLogin = false; }
    }

    public Task ActivateAccountAsync(string url, string username) => ViewModel.ActivateAccountAsync(url, username);
    public void OfflineStatus() => ViewModel.OfflineStatus();
    public async Task SynchronizeAsync(LoginWindow login, string url, string username, string password, bool remember, bool fullRefresh)
    {
        try
        {
            await ViewModel.SynchronizeAsync(url, username, password, remember, fullRefresh,
                new Progress<string>(text => login.StatusText = text));
            login.Complete();
        }
        catch (ObkException error) { login.StatusText = error.Message + " Existing offline messages remain available."; }
        finally { login.SetBusy(false); }
    }

    private async Task<string> ExportCopyAsync(Login account, AttachmentRow row, string? destination = null)
    {
        return await Task.Run(() =>
        {
            if (destination is null)
            {
                var directory = Path.Combine(Path.GetDirectoryName(Store.Path)!, "opened", "attachment-" + Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(directory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ObkException("Cannot create a decrypted attachment copy folder. Check disk access.");
                }
                destination = Path.Combine(directory, row.Name);
            }
            return Store.ExportAttachment(account.Url, account.Username, row.Attachment.Identifier, destination);
        });
    }

    private async Task ExportAsync(AttachmentRow row, string action)
    {
        if (!ViewModel.CanChangeAccount || Account is not { } account) return;
        if (action == "open" && !row.CanOpen)
            throw new ObkException("This file type cannot be opened directly. Use Save as.");
        string? destination = null;
        if (action == "save")
        {
            var dialog = new SaveFileDialog { Title = "Save a decrypted attachment copy", FileName = row.Name, DefaultExt = Path.GetExtension(row.Name) };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
        }
        ViewModel.IsExporting = true;
        ViewModel.SetStatus("Preparing attachment copy...");
        try
        {
            var path = await ExportCopyAsync(account, row, destination);
            if (action is "open" or "folder")
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (action == "folder")
                        {
                            var start = new ProcessStartInfo("explorer.exe")
                            {
                                UseShellExecute = true,
                                Arguments = "/select,\"" + path + "\""
                            };
                            Process.Start(start);
                        }
                        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    });
                }
                catch (Win32Exception)
                {
                    throw new ObkException("The copy was created, but Windows could not open it. Install a default viewer or use Save as.");
                }
            }
            ViewModel.SetStatus(action == "save" ? $"Saved attachment copy: {row.Name}" :
                $"Opened decrypted copy: {row.Name}. It remains in the local opened folder.", true);
        }
        finally { ViewModel.IsExporting = false; }
    }

    private async Task SaveAllAsync(IReadOnlyList<AttachmentRow> rows)
    {
        if (!ViewModel.CanChangeAccount || Account is not { } account) return;
        var dialog = new OpenFolderDialog { Title = "Save all decrypted attachment copies" };
        if (dialog.ShowDialog(this) != true) return;
        var destinations = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var name = row.Name;
            var index = 2;
            while (!names.Add(name))
                name = $"{Path.GetFileNameWithoutExtension(row.Name)} ({index++}){Path.GetExtension(row.Name)}";
            destinations.Add(Path.Combine(dialog.FolderName, name));
        }
        if (await Task.Run(() => destinations.Any(File.Exists)) && MessageBox.Show(this,
            "Some files already exist in this folder. Replace those files with the attachment copies?",
            "Replace files?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ViewModel.IsExporting = true;
        var saved = 0;
        try
        {
            for (; saved < rows.Count; saved++)
            {
                ViewModel.SetStatus($"Saving attachment {saved + 1} of {rows.Count}...");
                await ExportCopyAsync(account, rows[saved], destinations[saved]);
            }
            ViewModel.SetStatus($"Saved {saved} decrypted attachment copies. Delete copies when no longer needed.", true);
        }
        catch (ObkException error) { throw new ObkException($"{error.Message} Saved {saved} of {rows.Count} copies."); }
        finally { ViewModel.IsExporting = false; }
    }

    private void AttachmentMouseDown(object sender, MouseButtonEventArgs e) => dragOrigin = e.GetPosition(this);
    private void ReaderMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var lines = SystemParameters.WheelScrollLines;
        var step = lines < 0 ? ReadingScroll.ViewportHeight : lines * 21.7;
        ReadingScroll.ScrollToVerticalOffset(ReadingScroll.VerticalOffset - e.Delta / 120d * step);
        e.Handled = true;
    }

    private async void AttachmentMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Button { DataContext: AttachmentRow { Cached: true } row } ||
            !ViewModel.CanChangeAccount || Account is not { } account) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled = true;
        Mouse.Capture(null);
        ViewModel.IsExporting = true;
        try
        {
            ViewModel.SetStatus("Preparing attachment for Windows drag-and-drop...");
            var path = await ExportCopyAsync(account, row);
            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
                ViewModel.SetStatus("Attachment drag finished. The decrypted source copy remains in the local opened folder.", true);
            }
            else ViewModel.SetStatus("Drag ended before the copy was ready. Drag the attachment again.", true);
        }
        catch (ObkException error) { ViewModel.ReportError(error.Message); }
        finally { ViewModel.IsExporting = false; }
    }

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ChromeHook);
    }

    private IntPtr ChromeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int nonClientHitTest = 0x0084;
        const int maxButton = 9;
        if (message == nonClientHitTest)
        {
            var coordinates = lParam.ToInt64();
            var point = PointFromScreen(new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff)));
            var bounds = new Rect(MaximizeButton.TranslatePoint(new Point(), this), MaximizeButton.RenderSize);
            if (bounds.Contains(point)) { handled = true; return new IntPtr(maxButton); }
        }
        // HTMAXBUTTON enables the Windows 11 snap flyout; caption clicks arrive as non-client messages.
        if (message == 0x00A1 && wParam.ToInt32() == maxButton) { handled = true; }
        if (message == 0x00A2 && wParam.ToInt32() == maxButton)
        {
            MaximizeCommand.Execute(null);
            handled = true;
        }
        return IntPtr.Zero;
    }

    internal async Task VerifyOfflineReaderAsync()
    {
        await ViewModel.FolderReady;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Messages.SelectedIndex = 0;
        await ViewModel.MessageReady;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (Subject.Text != "Installation check" ||
            !new TextRange(ContentReader.Document.ContentStart, ContentReader.Document.ContentEnd).Text.Contains("Offline content") ||
            !ViewModel.VisibleAttachments.First().Cached || ViewModel.VisibleAttachments.Count != 3)
            throw new ObkException("The offline reader did not render its synthetic message correctly.");
        ViewModel.ExpandAttachmentsCommand.Execute(null);
        if (ViewModel.VisibleAttachments.Count != 6)
            throw new ObkException("The attachment strip did not expand to show every file.");
        ViewModel.ExpandAttachmentsCommand.Execute(null);
        ViewModel.SelectedMessage = ViewModel.Messages[1];
        await ViewModel.MessageReady;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        ContentReader.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent
        });
        UpdateLayout();
        if (ReadingScroll.VerticalOffset <= 0)
            throw new ObkException("The selectable message body did not scroll with the mouse wheel.");
        ViewModel.SelectedMessage = ViewModel.Messages[0];
        await ViewModel.MessageReady;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ReadingScroll.ScrollToTop();
    }
}
