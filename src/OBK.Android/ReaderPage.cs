using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class ReaderPage : ContentPage
{
    private readonly ImageButton menu = ReaderTheme.Icon("menu", AppStrings.ReaderNavigationMenu);
    private readonly ImageButton folder = ReaderTheme.Icon("inbox", SharedStrings.FolderInbox);
    private readonly Button child = ReaderTheme.Primary(new Button
    {
        WidthRequest = 48, HeightRequest = 48, Padding = 0, CornerRadius = 24,
        FontSize = 16, FontAttributes = FontAttributes.Bold
    });
    private readonly SearchBar search = new() { Placeholder = AppStrings.ReaderSearchPlaceholder, BackgroundColor = Colors.Transparent };
    private readonly Label status = ReaderTheme.Metadata(new Label { Text = AppStrings.ReaderStatusOpening });
    private readonly ImageButton refresh = ReaderTheme.Icon("refresh", AppStrings.ReaderRefreshAll);
    private readonly ActivityIndicator progress = new() { IsVisible = false };
    private readonly CollectionView messages = new() { SelectionMode = SelectionMode.Single };
    private readonly Label empty = new();
    private readonly Button addChild = new() { Text = AppStrings.ReaderAccountsSheetAddChild, IsVisible = false };
    private ReaderSession? session;
    private IReadOnlyList<Login> known = [];
    private Archive? archive;
    private Login? account;
    private bool busy;
    private int folderIndex;
    private bool initialized;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private static readonly string[] FolderKeys = ["received", "noticeboard", "sent", "excuses"];
    private static readonly string[] FolderIcons = ["inbox", "noticeboard", "sent", "excuses"];
    private readonly string[] folderLabels = [SharedStrings.FolderInbox, SharedStrings.FolderNoticeboard, SharedStrings.FolderSent, SharedStrings.FolderExcuses];

    public ReaderPage(Action openMenu)
    {
        Title = "OBK";
        NavigationPage.SetHasNavigationBar(this, false);
        menu.Clicked += (_, _) => openMenu();
        child.Clicked += async (_, _) =>
        {
            var choices = known.Select((login, index) =>
                Localized.Format(AppStrings.ReaderAccountOption, index + 1, AccountLabelConverter.Label(login))).ToArray();
            var selected = await DisplayActionSheetAsync(AppStrings.ReaderAccountPickerTitle, AppStrings.ReaderAccountsSheetCancel, null, choices);
            var index = Array.IndexOf(choices, selected);
            if (index >= 0) await RunAsync(() => SelectAsync(known[index]));
        };
        folder.Clicked += async (_, _) =>
        {
            var selected = await DisplayActionSheetAsync(AppStrings.ReaderFolderPickerTitle, AppStrings.ReaderAccountsSheetCancel, null, folderLabels);
            var index = Array.IndexOf(folderLabels, selected);
            if (index < 0) return;
            folderIndex = index;
            UpdateContext();
            ApplySearch();
        };
        search.TextChanged += (_, _) => ApplySearch();
        search.HandlerChanged += (_, _) =>
        {
            if (search.Handler?.PlatformView is global::AndroidX.AppCompat.Widget.SearchView native)
                native.FindViewById(Resource.Id.search_plate)?.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
        };
        refresh.Clicked += async (_, _) => await RunAsync(session is null ? InitializeAsync : RefreshAllAsync);
        messages.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold, MaxLines = 2 };
            title.SetBinding(Label.TextProperty, nameof(Message.Title));
            var sender = ReaderTheme.Metadata(new Label { MaxLines = 1 });
            sender.SetBinding(Label.TextProperty, nameof(Message.SenderName));
            var date = ReaderTheme.Metadata(new Label());
            date.SetBinding(Label.TextProperty, new Binding(nameof(Message.Date), stringFormat: "{0:g}"));
            return ReaderTheme.Apply(new VerticalStackLayout
            {
                Padding = new Thickness(16, 12),
                Spacing = 4,
                Children = { title, sender, date, new BoxView { HeightRequest = 1 } }
            });
        });
        addChild.Clicked += async (_, _) => await OpenSignInAsync();
        messages.EmptyView = new VerticalStackLayout { Padding = 24, Spacing = 12, Children = { empty, addChild } };
        messages.SelectionChanged += async (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is Message message && session is not null && account is not null)
            {
                messages.SelectedItem = null;
                await RunAsync(() => Navigation.PushAsync(new MessagePage(session, account, message)));
            }
        };
        var searchContent = new Grid { ColumnDefinitions = [new(48), new(GridLength.Star), new(48)] };
        searchContent.Add(folder, 0);
        searchContent.Add(search, 1);
        searchContent.Add(child, 2);
        var searchPanel = ReaderTheme.Panel(searchContent, 0);
        searchPanel.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 };
        var header = new Grid
        {
            Padding = new Thickness(8, 12), ColumnSpacing = 4,
            ColumnDefinitions = [new(48), new(GridLength.Star), new(48)]
        };
        header.Add(menu, 0);
        header.Add(searchPanel, 1);
        header.Add(refresh, 2);
        var footer = new Grid { Padding = new Thickness(16, 8), ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 8 };
        progress.WidthRequest = progress.HeightRequest = 24;
        footer.Add(progress, 0);
        footer.Add(status, 1);
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(header);
        layout.Add(messages, 0, 1);
        layout.Add(footer, 0, 2);
        messages.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Surface, ReaderTheme.DarkSurface);
        Content = layout;
        ReaderTheme.Apply(this);
        ReaderTheme.Apply(empty);
        UpdateContext();
        SetBusy(false);
    }

    internal Task OpenSettingsAsync() => Navigation.PushAsync(new SettingsPage(
        AccountActionsAsync, () => session is not null, () => account is null ? null : AccountLabelConverter.Label(account)));

    private void UpdateContext()
    {
        ReaderTheme.SetIcon(folder, FolderIcons[folderIndex]);
        SemanticProperties.SetDescription(folder, Localized.Format(AppStrings.ReaderSelectedFolder, folderLabels[folderIndex]));
        var label = account is null ? null : AccountLabelConverter.Label(account);
        child.Text = label is null ? "" : string.Concat(label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2).Select(word => System.Globalization.StringInfo.GetNextTextElement(word))).ToUpperInvariant();
        child.ImageSource = label is null ? ImageSource.FromFile("child.png") : null;
        child.SetAppThemeColor(BackgroundColorProperty, label is null ? Colors.Transparent : ReaderTheme.Accent,
            label is null ? Colors.Transparent : ReaderTheme.DarkAccent);
        SemanticProperties.SetDescription(child, label is null ? AppStrings.ReaderAccountPickerTitle :
            Localized.Format(AppStrings.ReaderSelectedChild, label));
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
        menu.IsEnabled = !value;
        child.IsEnabled = !value && known.Count > 0;
        folder.IsEnabled = !value;
        search.IsEnabled = !value;
        messages.IsEnabled = !value;
        SemanticProperties.SetDescription(refresh, session is null ? AppStrings.ReaderRetryOpening : AppStrings.ReaderRefreshAll);
        refresh.IsEnabled = !value && (session is null || known.Count > 0);
        addChild.IsEnabled = !value && session is not null;
    }

    private async Task RunAsync(Func<Task> action)
    {
        await operationGate.WaitAsync();
        SetBusy(true);
        try { await action(); }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            UpdateContext();
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
        var selected = preferred ?? account ?? result.Item2;
        var index = selected is null ? -1 : known.ToList().FindIndex(item =>
            item.Url == selected.Url && item.Username == selected.Username);
        if (index < 0 && known.Count > 0) index = 0;
        if (index >= 0) await SelectAsync(known[index]);
        else
        {
            account = null;
            archive = null;
            UpdateContext();
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
        UpdateContext();
        search.Text = "";
        status.Text = loaded is null ? AppStrings.ReaderStatusNoArchive :
            Localized.Format(AppStrings.ReaderStatusArchiveReady, DateTimeOffset.Parse(loaded.SyncedAt));
        ApplySearch();
    }

    private void ApplySearch()
    {
        var key = FolderKeys[folderIndex];
        IEnumerable<Message> items = archive is null ? [] :
            key == "excuses" ? archive.Folders["sent"].Where(MessageInsights.IsExcuse) :
            key == "sent" ? archive.Folders["sent"].Where(item => !MessageInsights.IsExcuse(item)) :
            archive.Folders[key];
        var query = search.Text?.Trim() ?? "";
        if (query.Length > 0)
            items = items.Where(item => $"{item.Title}\n{item.SenderName}\n{item.Text}\n{item.SentDate}\n{item.Date:g}"
                .Contains(query, StringComparison.CurrentCultureIgnoreCase));
        messages.ItemsSource = items.OrderByDescending(item => item.Date).ToList();
        addChild.IsVisible = session is not null && known.Count == 0;
        empty.Text = query.Length > 0 ? AppStrings.ReaderEmptySearch :
            archive is null ? AppStrings.ReaderEmptyNoArchive : AppStrings.ReaderEmptyFolder;
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
                await DisplayAlertAsync(AppStrings.ReaderAccountsSheetTitle, status.Text, AppStrings.AlertOkButton);
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

    private static class AccountLabelConverter
    {
        public static string Label(Login login) => !string.IsNullOrWhiteSpace(login.ChildName) ? login.ChildName :
            (login.SchoolName is { } school ? Localized.Format(AppStrings.AccountSchoolAndUsername, school, login.Username) : login.Username);
    }
}
