using Microsoft.Win32;
using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
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
    public ICommand RenameAccountCommand { get; }
    public ICommand SetPasswordCommand { get; }
    public ICommand ClearPasswordCommand { get; }
    public ICommand TransferAccountCommand { get; }
    public string VersionText { get; } = UpdateService.Display(UpdateService.InstalledVersion);
    private readonly bool showLogin;
    private Task? initialization;
    private Point dragOrigin;
    private bool compact;
    private bool openingLogin;
    private bool replacing;
    private bool switchingLanguage;
    private readonly bool enableUpdates;
    private int? cornerPreferenceError;
    private static bool buildAcknowledged;

    public UpdateManager Updates { get; }
    public string LanguageCode => LanguageSettings.Code.ToUpperInvariant();
    public string LanguageTooltip => LanguageSettings.SwitchTooltip;
    public MainWindow(LocalStore store, bool showLogin = true, bool enableUpdates = true, UpdateManager? updates = null,
        ThemeManager? appearance = null)
    {
        Store = store;
        this.showLogin = showLogin;
        this.enableUpdates = enableUpdates;
        MinimizeCommand = new RelayCommand(_ => SystemCommands.MinimizeWindow(this));
        MaximizeCommand = new RelayCommand(_ =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        });
        CloseCommand = new RelayCommand(_ => Close());
        var theme = appearance ?? new ThemeManager(Path.Combine(Path.GetDirectoryName(store.Path)!, "appearance.json"));
        ViewModel = new MainViewModel(store, theme, OpenLoginDialog, ExportAsync, SaveAllAsync);
        ViewModel.RequestMarksPassword = login =>
        {
            var prompt = new PromptWindow(this, SharedStrings.MarksTitle,
                SharedStrings.MarksCurrentPeriod, Localized.Format(SharedStrings.MarksPassword, AccountRow.LabelFor(login)),
                AppStrings.MainRefresh, _ => Task.CompletedTask, password: true);
            return Task.FromResult(prompt.ShowDialog() == true ? prompt.Value : null);
        };
        RenameAccountCommand = new RelayCommand(p => { if (p is AccountRow row) RenameAccount(row); },
            p => p is AccountRow && ViewModel.CanChangeAccount);
        SetPasswordCommand = new RelayCommand(p => { if (p is AccountRow row) SetPassword(row); },
            p => p is AccountRow && ViewModel.CanChangeAccount);
        ClearPasswordCommand = new AsyncCommand(p => p is AccountRow row ? ViewModel.ForgetPasswordAsync(row) : Task.CompletedTask,
            message => ViewModel.ReportError(message), p => p is AccountRow && ViewModel.CanChangeAccount);
        TransferAccountCommand = new RelayCommand(p => { if (p is AccountRow row) TransferAccount(row); },
            p => p is AccountRow { PasswordSaved: true } && ViewModel.CanChangeAccount);
        Updates = updates ?? new UpdateManager(new UpdateService(Path.Combine(Path.GetDirectoryName(store.Path)!, "updates")),
            UpdateService.InstalledVersion, () => !Busy && !ViewModel.IsExporting && !switchingLanguage, Close);
        InitializeComponent();
        HelpButton.ContextMenu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - 4), PopupPrimaryAxis.Horizontal)];
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(LanguageSettings.Culture.Name);
        var build = BuildIdentity.Current;
        BuildRibbonTimestamp.Text = build.TimestampText;
        BuildRibbon.ToolTip = Localized.Format(AppStrings.BuildRibbonTooltip, build.FullTimestampText);
        System.Windows.Automation.AutomationProperties.SetHelpText(BuildRibbon, (string)BuildRibbon.ToolTip);
        UpdateBuildRibbonVisibility();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            // Zero glass makes WPF install a window region, which prevents native DWM rounding.
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(1);
        }
        DataContext = ViewModel;
        UpdateText.DataContext = UpdateActions.DataContext = Updates;
        Closed += (_, _) =>
        {
            if (replacing) return;
            Updates.OnExit();
            Updates.Dispose();
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (enableUpdates && e.PropertyName is nameof(MainViewModel.Account) or nameof(MainViewModel.ShowingAllChildren) &&
                (ViewModel.Account is not null || ViewModel.ShowingAllChildren)) Updates.Start();
            if (e.PropertyName == nameof(MainViewModel.NavigationExpanded))
                NavigationColumn.Width = new GridLength(ViewModel.NavigationExpanded ? expandedNavigationWidth : IconNavigationWidth);
            if (e.PropertyName != nameof(MainViewModel.SelectedMessage)) return;
            ReadingScroll.ScrollToTop();
            if (ViewModel.SelectedMessage is { } row)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (ReferenceEquals(ViewModel.SelectedMessage, row)) Messages.ScrollIntoView(row);
                }));
            }
        };
    }

    public Task InitializeAsync() => initialization ??= ViewModel.InitializeAsync();
    private void OpenRepository(object sender, RoutedEventArgs e) =>
        LinkOpener.Open(((Hyperlink)sender).NavigateUri.AbsoluteUri, ViewModel.ReportError);

    private void OpenHelpMenu(object sender, RoutedEventArgs e)
    {
        HelpButton.ContextMenu.PlacementTarget = HelpButton;
        HelpButton.ContextMenu.IsOpen = !HelpButton.ContextMenu.IsOpen;
    }

    private void OpenHelpLink(object sender, RoutedEventArgs e) =>
        LinkOpener.Open((string)((MenuItem)sender).Tag, ViewModel.ReportError);

    private async void SwitchLanguage(object sender, RoutedEventArgs e)
    {
        if (switchingLanguage) return;
        LanguageButton.IsEnabled = false;
        try { await ChangeLanguageAsync(); }
        catch (ObkException error) { ViewModel.ReportError(error.Message); }
        finally
        {
            LanguageButton.SetBinding(IsEnabledProperty, nameof(MainViewModel.CanChangeAccount));
        }
    }

    public async Task<MainWindow> ChangeLanguageAsync()
    {
        if (switchingLanguage || !ViewModel.CanChangeAccount) throw new ObkException(SharedStrings.ErrorLanguageBusy);
        switchingLanguage = true;
        WindowRoot.IsEnabled = false;
        try
        {
            await ViewModel.FolderReady;
            await ViewModel.MessageReady;
            await ViewModel.OpenedSaved;
            var state = ViewModel.CaptureState();
            var next = LanguageSettings.NextCode;
            await LanguageSettings.SaveAsync(Path.Combine(Path.GetDirectoryName(Store.Path)!, "language.json"), next);
            LanguageSettings.Apply(next);
            AppStrings.Culture = LanguageSettings.Culture;
            var replacement = new MainWindow(Store, showLogin: false, enableUpdates, Updates, ViewModel.Theme);
            replacement.initialization = replacement.ViewModel.RestoreStateAsync(state);
            await replacement.InitializeAsync();
            replacement.expandedNavigationWidth = expandedNavigationWidth;
            var bounds = WindowState == WindowState.Normal ?
                new Rect(Left, Top, Width, Height) : RestoreBounds;
            replacement.Left = bounds.Left;
            replacement.Top = bounds.Top;
            replacement.Width = bounds.Width;
            replacement.Height = bounds.Height;
            replacement.WindowState = WindowState;
            var revealed = FlashFreeReveal.Prepare(replacement);
            replacement.Show();
            replacement.NavigationColumn.Width = new GridLength(state.NavigationExpanded ? expandedNavigationWidth : IconNavigationWidth);
            replacement.MessageColumn.Width = MessageColumn.Width;
            replacement.AppearanceSection.IsExpanded = AppearanceSection.IsExpanded;
            replacement.RefreshSection.IsExpanded = RefreshSection.IsExpanded;
            replacement.AccountsSection.IsExpanded = AccountsSection.IsExpanded;
            replacement.AboutSection.IsExpanded = AboutSection.IsExpanded;
            Updates.SetHost(() => !replacement.Busy && !replacement.ViewModel.IsExporting && !replacement.switchingLanguage, replacement.Close);
            await revealed;
            Application.Current.MainWindow = replacement;
            replacing = true;
            Close();
            replacement.Activate();
            return replacement;
        }
        finally { switchingLanguage = false; WindowRoot.IsEnabled = true; }
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeAsync();
        if (cornerPreferenceError is { } error)
            ViewModel.ReportError(Localized.Format(AppStrings.ErrorRoundedCorners, error));
        if (showLogin && ViewModel.Accounts.Count == 0) OpenLoginDialog(true);
    }

    private void UpdateMenuClick(object sender, RoutedEventArgs e) => Updates.MenuOpen = !Updates.MenuOpen;

    private void BuildRibbonClick(object sender, RoutedEventArgs e)
    {
        buildAcknowledged = true;
        UpdateBuildRibbonVisibility();
    }

    private void UpdateBuildRibbonVisibility()
    {
        var visible = BuildIdentity.Current.IsLocal && !buildAcknowledged;
        BuildRibbonArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // Clicking the already-selected folder does not change selection, so close Settings on any folder click.
    private void FolderListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(FolderList, source) is ListBoxItem)
            ViewModel.SettingsOpen = false;
    }

    // A new message that was selected automatically is opened by clicking it or pressing Enter or Space on it.
    private void MessageListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(Messages, source) is ListBoxItem { IsSelected: true, DataContext: MessageRow row })
            ViewModel.MarkOpened(row);
    }

    private void MessageListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && e.OriginalSource is ListBoxItem { IsSelected: true, DataContext: MessageRow row })
            ViewModel.MarkOpened(row);
    }

    private void RenameAccount(AccountRow row)
    {
        var school = row.Login.SchoolName ?? row.Login.Username;
        new PromptWindow(this, AppStrings.PromptRenameTitle,
            Localized.Format(AppStrings.PromptRenameDescription, school),
            AppStrings.FieldChildName, AppStrings.PromptRenameAction, value => ViewModel.RenameAccountAsync(row, value), initial: row.Login.ChildName ?? "").ShowDialog();
    }

    private void SetPassword(AccountRow row)
    {
        var school = row.Login.SchoolName ?? row.Login.Url;
        new PromptWindow(this,
            Localized.Format(row.PasswordSaved ? AppStrings.SettingsChangePasswordFor : AppStrings.SettingsSavePasswordFor, row.Label),
            Localized.Format(AppStrings.PromptPasswordDescription, school, row.Login.Username),
            AppStrings.FieldPassword, AppStrings.PromptPasswordAction, password => ViewModel.VerifyAndRememberPasswordAsync(row, password), password: true).ShowDialog();
    }

    private void TransferAccount(AccountRow row)
    {
        var login = row.Login;
        new TransferWindow(this, row.Label, async () => await Task.Run(() =>
            Store.RememberedLogins().LastOrDefault(profile => profile.Url == login.Url && profile.Username == login.Username)) ??
            throw new ObkException(SharedStrings.ErrorTransferNeedsPassword)).ShowDialog();
    }

    private const double IconNavigationWidth = 64;
    private const double IconSnapWidth = 140;
    private double expandedNavigationWidth = 210;

    // Dragging the sidebar narrower than readable labels snaps it to icon-only mode.
    private void NavigationSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var width = NavigationColumn.ActualWidth;
        if (width < IconSnapWidth)
        {
            if (ViewModel.NavigationExpanded) ViewModel.NavigationExpanded = false;
            else NavigationColumn.Width = new GridLength(IconNavigationWidth);
            return;
        }
        expandedNavigationWidth = width;
        if (!ViewModel.NavigationExpanded) ViewModel.NavigationExpanded = true;
    }
    private void UpdateTextClick(object sender, MouseButtonEventArgs e)
    {
        if (Updates.CheckNowCommand.CanExecute(null)) Updates.CheckNowCommand.Execute(null);
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.Loading || (ViewModel.IsSyncing && !ViewModel.ReadingDuringDownload) ||
            ViewModel.IsExporting || ViewModel.Marks.Busy || (switchingLanguage && !replacing))
        {
            e.Cancel = true;
            Updates.CancelRestart();
            ViewModel.ReportError(AppStrings.ErrorCloseBusy);
        }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NavigationColumn is null) return;
        var next = ActualWidth < 1120;
        if (next != compact)
        {
            compact = next;
            expandedNavigationWidth = next ? 200 : 210;
            if (ViewModel.NavigationExpanded) NavigationColumn.Width = new GridLength(expandedNavigationWidth);
            MessageColumn.Width = new GridLength(next ? 260 : 360);
        }
        var maxListWidth = ActualWidth - 48 - NavigationColumn.ActualWidth - 300;
        if (MessageColumn.ActualWidth > maxListWidth)
            MessageColumn.Width = new GridLength(Math.Max(240, maxListWidth));
        WindowRoot.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private async void OpenLoginDialog(bool showSavedAccounts = false, Login? target = null)
    {
        if (openingLogin || !ViewModel.CanChangeAccount) return;
        openingLogin = true;
        try
        {
            Login? initial = showSavedAccounts ? null : target ?? Account;
            var remembered = false;
            var error = "";
            try
            {
                if (!showSavedAccounts)
                {
                    var saved = initial is null ? null : await Task.Run(() =>
                        Store.RememberedLogins().LastOrDefault(profile =>
                            profile.Url == initial.Url && profile.Username == initial.Username));
                    if (saved is not null) { initial = saved; remembered = true; }
                    else initial ??= await Task.Run(Store.LastAccount);
                }
            }
            catch (ObkException problem) { error = problem.Message; }
            var host = this;
            LoginWindow.FormState? form = null;
            while (true)
            {
                var dialog = new LoginWindow(host, initial, remembered, error, showSavedAccounts);
                if (form is not null)
                {
                    await dialog.RestoreFormAsync(form);
                    _ = FlashFreeReveal.Prepare(dialog);
                }
                if (error.Length > 0) dialog.StatusText = error;
                host.openingLogin = true;
                dialog.ShowDialog();
                host.openingLogin = false;
                if (dialog.LanguageForm is not { } preserved) break;
                form = preserved;
                try { host = await host.ChangeLanguageAsync(); error = ""; }
                catch (ObkException problem) { error = problem.Message; }
            }
        }
        catch (ObkException problem) { ((MainWindow)Application.Current.MainWindow).ViewModel.ReportError(problem.Message); }
        finally { openingLogin = false; }
    }

    public Task ActivateAccountAsync(string url, string username, string? childName = null, string? schoolName = null) =>
        ViewModel.ActivateAccountAsync(url, username, childName: childName, schoolName: schoolName);
    public void OfflineStatus() => ViewModel.OfflineStatus();
    private void ShowDownloadDetails(object sender, RoutedEventArgs e) =>
        DownloadDetailsPopup.IsOpen = !DownloadDetailsPopup.IsOpen;
    public async Task SynchronizeAsync(LoginWindow login, string url, string username, string password, bool remember,
        bool fullRefresh, string? childName, string? schoolName)
    {
        try
        {
            await ViewModel.SynchronizeAsync(url, username, password, remember, fullRefresh, childName, schoolName,
                login.SetDownloadState);
            if (!login.Completed) login.Complete();
        }
        catch (ObkException error)
        {
            if (!login.Completed) login.StatusText = ViewModel.DownloadState is { Error: { } failure } state && failure == error.Message
                ? state.Status : Localized.Format(AppStrings.LoginSyncFailed, error.Message);
        }
        finally { login.SetBusy(false); }
    }

    private async Task<string> ExportCopyAsync(AttachmentRow row, string? destination = null)
    {
        var account = row.Account;
        return await Task.Run(() =>
        {
            if (destination is null)
            {
                var directory = Path.Combine(Path.GetDirectoryName(Store.Path)!, "opened", "attachment-" + Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(directory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ObkException(AppStrings.ErrorAttachmentFolder);
                }
                destination = Path.Combine(directory, row.Name);
            }
            return Store.ExportAttachment(account.Url, account.Username, row.Attachment.Identifier, destination);
        });
    }

    private async Task ExportAsync(AttachmentRow row, string action)
    {
        if (!ViewModel.CanUseAttachments) return;
        if (action == "open" && !row.CanOpen)
            throw new ObkException(AppStrings.ErrorAttachmentCannotOpen);
        string? destination = null;
        if (action == "save")
        {
            var dialog = new SaveFileDialog { Title = AppStrings.DialogSaveAttachmentTitle, FileName = row.Name, DefaultExt = Path.GetExtension(row.Name) };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
        }
        ViewModel.IsExporting = true;
        ViewModel.SetStatus(AppStrings.StatusPreparingAttachment);
        try
        {
            var path = await ExportCopyAsync(row, destination);
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
                    throw new ObkException(AppStrings.ErrorAttachmentNoViewer);
                }
            }
            ViewModel.SetStatus(Localized.Format(
                action == "save" ? AppStrings.StatusAttachmentSaved : AppStrings.StatusAttachmentOpened, row.Name), true);
        }
        finally { ViewModel.IsExporting = false; }
    }

    private async Task SaveAllAsync(IReadOnlyList<AttachmentRow> rows)
    {
        if (!ViewModel.CanUseAttachments || rows.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = AppStrings.DialogSaveAllTitle };
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
            AppStrings.DialogReplaceFilesMessage,
            AppStrings.DialogReplaceFilesTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ViewModel.IsExporting = true;
        var saved = 0;
        try
        {
            for (; saved < rows.Count; saved++)
            {
                ViewModel.SetStatus(Localized.Format(AppStrings.StatusSavingAttachment, saved + 1, rows.Count));
                await ExportCopyAsync(rows[saved], destinations[saved]);
            }
            ViewModel.SetStatus(Localized.Format(AppStrings.StatusAttachmentsSaved, saved), true);
        }
        catch (ObkException error) { throw new ObkException(Localized.Format(AppStrings.ErrorSaveAllPartial, error.Message, saved, rows.Count)); }
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
            !ViewModel.CanUseAttachments) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled = true;
        Mouse.Capture(null);
        ViewModel.IsExporting = true;
        try
        {
            ViewModel.SetStatus(AppStrings.StatusPreparingDrag);
            var path = await ExportCopyAsync(row);
            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
                ViewModel.SetStatus(AppStrings.StatusDragFinished, true);
            }
            else ViewModel.SetStatus(AppStrings.StatusDragTooEarly, true);
        }
        catch (ObkException error) { ViewModel.ReportError(error.Message); }
        finally { ViewModel.IsExporting = false; }
    }

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(ChromeHook);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            const int windowCornerPreference = 33;
            var round = 2;
            var result = DwmSetWindowAttribute(handle, windowCornerPreference, ref round, sizeof(int));
            if (result < 0) cornerPreferenceError = result;
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
        await ViewModel.MessageReady;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (ViewModel.SelectedMessage?.Message.Id != "fixture-second" ||
            !ReferenceEquals(Messages.SelectedItem, ViewModel.SelectedMessage))
            throw new ObkException("The newest message was not selected when opening the folder.");
        ViewModel.SelectedMessage = ViewModel.Messages.Single(m => m.Message.Id == "installation-check");
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
        ViewModel.SelectedMessage = ViewModel.Messages.Single(m => m.Message.Id == "fixture-second");
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
        ViewModel.SelectedMessage = ViewModel.Messages.Single(m => m.Message.Id == "installation-check");
        await ViewModel.MessageReady;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ReadingScroll.ScrollToTop();
        ViewModel.SelectedFolder = ViewModel.Navigation.Single(f => f.Key == "sent");
        await ViewModel.FolderReady;
        await ViewModel.MessageReady;
        ViewModel.SelectedFolder = ViewModel.Navigation.Single(f => f.Key == "received");
        await ViewModel.FolderReady;
        await ViewModel.MessageReady;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (ViewModel.SelectedMessage?.Message.Id != "installation-check" ||
            !ReferenceEquals(Messages.SelectedItem, ViewModel.SelectedMessage))
            throw new ObkException("Folder navigation did not restore the previously selected message.");
    }
}
