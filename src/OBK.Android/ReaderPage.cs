using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class ReaderPage : ContentPage
{
    private readonly Picker accounts = new() { Title = AppStrings.ReaderAccountPickerTitle };
    private readonly Picker folders = new() { Title = AppStrings.ReaderFolderPickerTitle };
    private readonly SearchBar search = new() { Placeholder = AppStrings.ReaderSearchPlaceholder };
    private readonly Label status = new() { Text = AppStrings.ReaderStatusOpening };
    private readonly Button refresh = new() { Text = AppStrings.ReaderRefreshAll, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Button signIn = new() { Text = AppStrings.ReaderAccountsButton, LineBreakMode = LineBreakMode.WordWrap };
    private readonly ActivityIndicator progress = new() { IsVisible = false };
    private readonly CollectionView messages = new() { SelectionMode = SelectionMode.Single };
    private readonly Label empty = new();
    private ReaderSession? session;
    private IReadOnlyList<Login> known = [];
    private Archive? archive;
    private Login? account;
    private bool busy;
    private bool loadingAccounts;
    private bool initialized;
    internal bool Busy => busy;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private static readonly string[] FolderKeys = ["received", "noticeboard", "sent", "excuses"];

    public ReaderPage()
    {
        Title = "OBK";
        LanguageToolbar.Add(this);
        folders.ItemsSource = new[] { SharedStrings.FolderInbox, SharedStrings.FolderNoticeboard, SharedStrings.FolderSent, SharedStrings.FolderExcuses };
        folders.SelectedIndex = 0;
        accounts.ItemDisplayBinding = new Binding(".", converter: new AccountLabelConverter());
        accounts.SelectedIndexChanged += async (_, _) =>
        {
            if (!loadingAccounts && accounts.SelectedIndex >= 0)
                await RunAsync(async () => await SelectAsync(known[accounts.SelectedIndex]));
        };
        folders.SelectedIndexChanged += (_, _) => ApplySearch();
        search.TextChanged += (_, _) => ApplySearch();
        refresh.Clicked += async (_, _) => await RunAsync(session is null ? InitializeAsync : RefreshAllAsync);
        signIn.Clicked += async (_, _) => await AccountActionsAsync();
        messages.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontAttributes = FontAttributes.Bold, MaxLines = 2 };
            title.SetBinding(Label.TextProperty, nameof(Message.Title));
            var sender = new Label { MaxLines = 1 };
            sender.SetBinding(Label.TextProperty, nameof(Message.SenderName));
            var date = new Label();
            date.SetBinding(Label.TextProperty, new Binding(nameof(Message.Date), converter: new DateConverter()));
            return ReaderTheme.Apply(new VerticalStackLayout
            {
                Padding = new Thickness(16, 12),
                Spacing = 4,
                Children = { title, sender, date, new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.25 } }
            });
        });
        messages.EmptyView = empty;
        messages.SelectionChanged += async (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is Message message && session is not null && account is not null)
            {
                messages.SelectedItem = null;
                await RunAsync(() => Navigation.PushAsync(new MessagePage(session, account, message)));
            }

        };
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(refresh, 0);
        actions.Add(signIn, 1);
        var header = new VerticalStackLayout
        {
            Padding = new Thickness(16, 8),
            Spacing = 8,
            Children = { accounts, actions, status, progress, folders, search }
        };
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
        layout.Add(header);
        layout.Add(messages, 0, 1);
        Content = layout;
        ReaderTheme.Apply(this);
        ReaderTheme.Apply(empty);
        SetBusy(false);
    }

    internal ReaderPage Rebuild()
    {
        var rebuilt = new ReaderPage
        {
            session = session,
            known = known,
            archive = archive,
            account = account,
            initialized = initialized,
            loadingAccounts = true
        };
        rebuilt.accounts.ItemsSource = known.ToList();
        rebuilt.accounts.SelectedIndex = accounts.SelectedIndex;
        rebuilt.folders.SelectedIndex = folders.SelectedIndex;
        rebuilt.search.Text = search.Text;
        rebuilt.loadingAccounts = false;
        rebuilt.status.Text = account is null ? AppStrings.ReaderStatusNoAccounts :
            archive is null ? AppStrings.ReaderStatusNoArchive : ArchiveStatus(archive);
        rebuilt.ApplySearch();
        rebuilt.SetBusy(false);
        return rebuilt;
    }

    internal Task AccountSavedAsync(Login login) => RunAsync(() => ReloadAsync(login));
    private static string ArchiveStatus(Archive archive) => Localized.Format(AppStrings.ReaderStatusArchiveReady,
        DateTimeOffset.Parse(archive.SyncedAt).ToLocalTime().ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture));

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initialized || busy) return;
        await RunAsync(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        session = await ReaderSession.OpenAsync();
        initialized = true;
        await ReloadAsync();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        progress.IsRunning = value;
        progress.IsVisible = value;
        accounts.IsEnabled = !value;
        folders.IsEnabled = !value;
        search.IsEnabled = !value;
        messages.IsEnabled = !value;
        refresh.Text = session is null ? AppStrings.ReaderRetryOpening : AppStrings.ReaderRefreshAll;
        refresh.IsEnabled = !value && (session is null || known.Count > 0);
        signIn.IsEnabled = !value && session is not null;
    }

    private async Task RunAsync(Func<Task> action)
    {
        await operationGate.WaitAsync();
        SetBusy(true);
        try { await action(); }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            loadingAccounts = true;
            accounts.SelectedIndex = account is null ? -1 : known.ToList().FindIndex(item =>
                item.Url == account.Url && item.Username == account.Username);
            loadingAccounts = false;
            status.Text = error.Message;
            progress.IsRunning = false;
            progress.IsVisible = false;
            await DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
        }
        finally
        {
            SetBusy(false);
            operationGate.Release();
        }
    }

    private async Task ReloadAsync(Login? preferred = null)
    {
        if (session is null) throw new ObkException(AppStrings.ErrorArchiveNotOpen);
        var result = await Task.Run(() => (session.Store.KnownLogins(), session.Store.LastAccount()));
        known = result.Item1;
        loadingAccounts = true;
        accounts.ItemsSource = known.ToList();
        var selected = preferred ?? account ?? result.Item2;
        var index = selected is null ? -1 : known.ToList().FindIndex(item =>
            item.Url == selected.Url && item.Username == selected.Username);
        if (index < 0 && known.Count > 0) index = 0;
        accounts.SelectedIndex = index;
        loadingAccounts = false;
        if (index >= 0) await SelectAsync(known[index]);
        else
        {
            account = null;
            archive = null;
            status.Text = AppStrings.ReaderStatusNoAccounts;
            ApplySearch();
        }
    }

    private async Task SelectAsync(Login login)
    {
        if (session is null) throw new ObkException(AppStrings.ErrorArchiveNotOpen);
        var loaded = await Task.Run(() =>
        {
            var value = session.Store.LoadArchive(login.Url, login.Username, SchoolYear.Current());
            session.Store.SaveAccount(login.Url, login.Username, login.ChildName, login.SchoolName);
            return value;
        });
        account = login;
        archive = loaded;
        search.Text = "";
        status.Text = loaded is null ? AppStrings.ReaderStatusNoArchive :
            ArchiveStatus(loaded);
        ApplySearch();
    }

    private void ApplySearch()
    {
        var key = FolderKeys[Math.Max(0, folders.SelectedIndex)];
        IEnumerable<Message> items = archive is null ? [] :
            key == "excuses" ? archive.Folders["sent"].Where(MessageInsights.IsExcuse) :
            key == "sent" ? archive.Folders["sent"].Where(item => !MessageInsights.IsExcuse(item)) :
            archive.Folders[key];
        var query = search.Text?.Trim() ?? "";
        if (query.Length > 0)
            items = items.Where(item => $"{item.Title}\n{item.SenderName}\n{item.Text}\n{item.SentDate}\n{MessageInsights.DateSearchText(item.Date)}"
                .Contains(query, StringComparison.CurrentCultureIgnoreCase));
        messages.ItemsSource = items.OrderByDescending(item => item.Date).ToList();
        empty.Text = query.Length > 0 ? AppStrings.ReaderEmptySearch :
            archive is null ? AppStrings.ReaderEmptyNoArchive : AppStrings.ReaderEmptyFolder;
        empty.Margin = new Thickness(24);
    }

    private async Task OpenSignInAsync(Login? login = null)
    {
        if (session is null || busy) return;
        await Navigation.PushAsync(new SignInPage(session, login, async saved =>
        {
            await RunAsync(() => ReloadAsync(saved));
        }));
    }

    private async Task AccountActionsAsync()
    {
        if (session is null || busy) return;
        if (account is null) { await OpenSignInAsync(); return; }
        var addChild = AppStrings.ReaderAccountsSheetAddChild;
        var signInAgain = AppStrings.ReaderAccountsSheetSignIn;
        var forgetPassword = AppStrings.ReaderAccountsSheetForgetPassword;
        var selected = await DisplayActionSheetAsync(AppStrings.ReaderAccountsSheetTitle, AppStrings.ReaderAccountsSheetCancel, null,
            addChild, signInAgain, forgetPassword);
        if (selected == addChild) await OpenSignInAsync();
        else if (selected == signInAgain) await OpenSignInAsync(account);
        else if (selected == forgetPassword)
            await RunAsync(async () =>
            {
                var current = account;
                await Task.Run(() => session.Store.ForgetPassword(current.Url, current.Username));
                await ReloadAsync();
                status.Text = AppStrings.ReaderStatusPasswordForgotten;
            });
    }

    private async Task RefreshAllAsync()
    {
        if (session is null) throw new ObkException(AppStrings.ErrorArchiveNotOpen);
        var saved = await Task.Run(() => session.Store.RememberedLogins());
        var failures = new List<string>();
        var missing = new List<Login>();
        var completed = 0;
        foreach (var login in known)
        {
            var credentials = saved.FirstOrDefault(item => item.Url == login.Url && item.Username == login.Username);
            if (credentials?.Password is not { } password)
            {
                missing.Add(login);
                continue;
            }
            var label = AccountLabelConverter.Label(login);
            status.Text = Localized.Format(AppStrings.ReaderAccountStatus, label, AppStrings.StatusSigningIn);
            try
            {
                await session.SynchronizeAsync(login, password, true,
                    new Progress<string>(text => status.Text = Localized.Format(AppStrings.ReaderAccountStatus, label, text)));
                completed++;
            }
            catch (ObkException error) { failures.Add(Localized.Format(AppStrings.ReaderAccountStatus, label, error.Message)); }
        }
        await ReloadAsync();
        var summary = new List<string> { Localized.Format(AppStrings.ReaderRefreshSummary, completed, known.Count) };
        if (missing.Count > 0)
            summary.Add(Localized.Format(AppStrings.ReaderRefreshSignInNeeded, string.Join(", ", missing.Select(AccountLabelConverter.Label))));
        if (failures.Count > 0)
            summary.Add(Localized.Format(AppStrings.ReaderRefreshFailed, string.Join("; ", failures)));
        status.Text = string.Join(" ", summary);
        if (failures.Count > 0 || missing.Count > 0)
            await DisplayAlertAsync(AppStrings.ReaderRefreshAlertTitle, status.Text, AppStrings.AlertOkButton);
        if (missing.Count > 0)
        {
            var login = missing.FirstOrDefault(item => item.Url == account?.Url && item.Username == account.Username) ?? missing[0];
            await Navigation.PushAsync(new SignInPage(session, login, savedLogin => RunAsync(() => ReloadAsync(savedLogin))));
        }
    }

    private sealed class AccountLabelConverter : IValueConverter
    {
        public static string Label(Login login) => login.ChildName ??
            (login.SchoolName is { } school ? Localized.Format(AppStrings.AccountSchoolAndUsername, school, login.Username) : login.Username);
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is Login login ? Label(login) : "";
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class DateConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is DateTimeOffset date ? date.ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture) : "";
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
