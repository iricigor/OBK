using OBK.Core;

namespace OBK.AndroidApp;

internal sealed class ReaderPage : ContentPage
{
    private readonly Picker accounts = new() { Title = "Choose a child or account" };
    private readonly Picker folders = new() { Title = "Message folder" };
    private readonly SearchBar search = new() { Placeholder = "Search subject, sender, text or date" };
    private readonly Label status = new() { Text = "Opening encrypted offline archive..." };
    private readonly Button refresh = new() { Text = "Refresh all" };
    private readonly Button signIn = new() { Text = "Add child / sign in" };
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
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private static readonly string[] FolderKeys = ["received", "noticeboard", "sent", "excuses"];

    public ReaderPage()
    {
        Title = "OBK";
        folders.ItemsSource = new[] { "Inbox", "Noticeboard", "Sent", "Excuses" };
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
            date.SetBinding(Label.TextProperty, new Binding(nameof(Message.Date), stringFormat: "{0:g}"));
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
        refresh.Text = session is null ? "Retry opening archive" : "Refresh all";
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
            await DisplayAlertAsync("OBK could not complete the operation", error.Message, "OK");
        }
        finally
        {
            SetBusy(false);
            operationGate.Release();
        }
    }

    private async Task ReloadAsync(Login? preferred = null)
    {
        if (session is null) throw new ObkException("The encrypted archive is not open.");
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
            status.Text = "Add a child to download messages. Reading cached messages never contacts your school.";
            ApplySearch();
        }
    }

    private async Task SelectAsync(Login login)
    {
        if (session is null) throw new ObkException("The encrypted archive is not open.");
        var loaded = await Task.Run(() =>
        {
            var value = session.Store.LoadArchive(login.Url, login.Username, SchoolYear.Current());
            session.Store.SaveAccount(login.Url, login.Username, login.ChildName, login.SchoolName);
            return value;
        });
        account = login;
        archive = loaded;
        search.Text = "";
        status.Text = loaded is null ? "No offline archive yet. Sign in to download messages." :
            $"Offline archive ready. Last sync: {DateTimeOffset.Parse(loaded.SyncedAt):g}";
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
            items = items.Where(item => $"{item.Title}\n{item.SenderName}\n{item.Text}\n{item.SentDate}\n{item.Date:g}"
                .Contains(query, StringComparison.CurrentCultureIgnoreCase));
        messages.ItemsSource = items.OrderByDescending(item => item.Date).ToList();
        empty.Text = query.Length > 0 ? "No cached messages match your search." :
            archive is null ? "Your downloaded messages will appear here." : "No cached messages in this folder.";
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
        var selected = await DisplayActionSheetAsync("Accounts", "Cancel", null,
            "Add child", "Sign in to this account", "Forget this account's saved password");
        if (selected == "Add child") await OpenSignInAsync();
        else if (selected == "Sign in to this account") await OpenSignInAsync(account);
        else if (selected == "Forget this account's saved password")
            await RunAsync(async () =>
            {
                var current = account;
                await Task.Run(() => session.Store.ForgetPassword(current.Url, current.Username));
                await ReloadAsync();
                status.Text = "Saved password forgotten. Offline messages and attachments are still available.";
            });
    }

    private async Task RefreshAllAsync()
    {
        if (session is null) throw new ObkException("The encrypted archive is not open.");
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
            status.Text = $"{label}: Signing in...";
            try
            {
                await session.SynchronizeAsync(login, password, true,
                    new Progress<string>(text => status.Text = $"{label}: {text}"));
                completed++;
            }
            catch (ObkException error) { failures.Add($"{label}: {error.Message}"); }
        }
        await ReloadAsync();
        status.Text = $"Refreshed {completed} of {known.Count} accounts." +
            (missing.Count > 0 ? $" Sign in needed: {string.Join(", ", missing.Select(AccountLabelConverter.Label))}." : "") +
            (failures.Count > 0 ? " Failed: " + string.Join("; ", failures) + " Existing offline archives were kept." : "");
        if (failures.Count > 0 || missing.Count > 0)
            await DisplayAlertAsync("Refresh needs attention", status.Text, "OK");
        if (missing.Count > 0)
        {
            var login = missing.FirstOrDefault(item => item.Url == account?.Url && item.Username == account.Username) ?? missing[0];
            await Navigation.PushAsync(new SignInPage(session, login, savedLogin => RunAsync(() => ReloadAsync(savedLogin))));
        }
    }

    private sealed class AccountLabelConverter : IValueConverter
    {
        public static string Label(Login login) => login.ChildName ??
            (login.SchoolName is { } school ? $"{school} ({login.Username})" : login.Username);
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is Login login ? Label(login) : "";
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
