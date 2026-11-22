using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class ReaderPage : ContentPage
{
    private readonly ImageButton menu = ReaderTheme.Icon("menu", AppStrings.ReaderNavigationMenu);
    private readonly Button child = ReaderTheme.Primary(new Button
    {
        WidthRequest = 56, HeightRequest = 56, Padding = 0, CornerRadius = 28,
        FontSize = 16, FontAttributes = FontAttributes.Bold
    });
    private readonly SearchBar search = new() { Placeholder = AppStrings.ReaderSearchPlaceholder, BackgroundColor = Colors.Transparent };
    private readonly Label status = ReaderTheme.Metadata(new Label
    {
        Text = AppStrings.ReaderStatusOpening, MaxLines = 1,
        LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center
    });
    private readonly ImageButton refresh = ReaderTheme.Icon("refresh", AppStrings.ReaderRefreshAll);
    private readonly Button language;
    private readonly ActivityIndicator progress = new() { IsVisible = false };
    private readonly CollectionView messages = new() { SelectionMode = SelectionMode.Single };
    private readonly Label empty = new();
    private readonly Button addChild = ReaderTheme.Primary(new Button { Text = AppStrings.ReaderAccountsSheetAddChild, IsVisible = false });
    private ReaderSession? session;
    private IReadOnlyList<Login> known = [];
    private Archive? archive;
    private Login? account;
    private bool busy;
    private int folderIndex;
    private bool initialized;
    internal bool Busy => busy;
    internal bool AccountsAvailable => session is not null && !busy;
    internal int FolderIndex => folderIndex;
    internal IReadOnlyList<string> FolderLabels => folderLabels;
    internal IReadOnlyList<Login> KnownAccounts => known;
    internal Login? ActiveAccount => account;
    private readonly RefreshStatusTimeout refreshStatusTimeout = new(TimeProvider.System);
    private readonly IDispatcherTimer refreshStatusTimer;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private static readonly string[] FolderKeys = ["received", "noticeboard", "sent", "excuses"];
    internal static readonly string[] FolderIcons = ["inbox", "noticeboard", "sent", "excuses"];
    private readonly string[] folderLabels = [SharedStrings.FolderInbox, SharedStrings.FolderNoticeboard, SharedStrings.FolderSent, SharedStrings.FolderExcuses];
    private sealed record MessageListItem(Message Message, bool IsAlternate);

    public ReaderPage(Action openMenu)
    {
        Title = "OBK";
        language = LanguageToolbar.CreateButton(this);
        NavigationPage.SetHasNavigationBar(this, false);
        menu.Clicked += (_, _) => openMenu();
        child.Clicked += async (_, _) => await Navigation.PushAsync(CreateAccountPickerPage());
        refreshStatusTimer = Dispatcher.CreateTimer();
        refreshStatusTimer.Interval = TimeSpan.FromSeconds(1);
        refreshStatusTimer.Tick += (_, _) =>
        {
            if (busy || !refreshStatusTimeout.TryExpire()) return;
            refreshStatusTimer.Stop();
            RestoreArchiveStatus();
        };
        search.TextChanged += (_, _) => ApplySearch();
        search.HandlerChanged += (_, _) =>
        {
            if (search.Handler?.PlatformView is global::AndroidX.AppCompat.Widget.SearchView native)
            {
                native.FindViewById(Resource.Id.search_plate)?.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
                if (native.FindViewById(Resource.Id.search_mag_icon) is { } magnifier)
                    magnifier.Visibility = global::Android.Views.ViewStates.Gone;
            }
        };
        refresh.Clicked += async (_, _) => await RunAsync(session is null ? InitializeAsync : RefreshAllAsync);
        messages.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontAttributes = FontAttributes.Bold, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
            title.SetDynamicResource(Label.FontSizeProperty, "ListSubjectSize");
            title.SetBinding(Label.TextProperty, nameof(Message.Title));
            var sender = ReaderTheme.Metadata(new Label { MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation }, "ListSenderSize");
            sender.SetBinding(Label.TextProperty, nameof(Message.SenderName));
            var date = ReaderTheme.Metadata(new Label { MaxLines = 1, VerticalOptions = LayoutOptions.Center }, "ListDateSize");
            date.SetBinding(Label.TextProperty, new Binding(nameof(Message.Date), converter: new DateConverter()));
            var row = new Grid
            {
                ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
                ColumnSpacing = 8,
                MinimumHeightRequest = 48
            };
            row.SetDynamicResource(Grid.PaddingProperty, "ListRowPadding");
            row.SetDynamicResource(Grid.RowSpacingProperty, "ListLineGap");
            row.Add(title);
            row.Add(date, 1);
            row.Add(sender, 0, 1);
            var item = new Grid { RowDefinitions = [new(GridLength.Auto), new(1)], RowSpacing = 0 };
            item.Add(row);
            item.Add(new BoxView(), 0, 1);
            item.BindingContextChanged += (_, _) =>
            {
                if (item.BindingContext is not MessageListItem entry) return;
                row.BindingContext = entry.Message;
                item.SetAppThemeColor(BackgroundColorProperty,
                    entry.IsAlternate ? ReaderTheme.AlternateSurface : ReaderTheme.Surface,
                    entry.IsAlternate ? ReaderTheme.DarkAlternateSurface : ReaderTheme.DarkSurface);
            };
            return ReaderTheme.Apply(item);
        });
        addChild.Clicked += async (_, _) => await OpenSignInAsync();
        messages.EmptyView = new VerticalStackLayout { Padding = 24, Spacing = 12, Children = { empty, addChild } };
        messages.SelectionChanged += async (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is MessageListItem entry && session is not null && account is not null)
            {
                messages.SelectedItem = null;
                await RunAsync(() => Navigation.PushAsync(new MessagePage(session, account, entry.Message)));
            }

        };
        var searchIcon = ReaderTheme.Icon("search", AppStrings.ReaderSearchPlaceholder);
        searchIcon.Clicked += (_, _) => search.Focus();
        var searchContent = new Grid { ColumnDefinitions = [new(56), new(GridLength.Star), new(48)] };
        searchContent.Add(search, 1);
        searchContent.Add(searchIcon, 2);
        var searchPanel = ReaderTheme.Panel(searchContent, 0);
        searchPanel.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 };
        searchPanel.HeightRequest = 48;
        searchPanel.VerticalOptions = LayoutOptions.Center;
        var searchContainer = new Grid { MinimumHeightRequest = 56, IsClippedToBounds = false };
        searchContainer.Add(searchPanel);
        child.HorizontalOptions = LayoutOptions.Start;
        child.VerticalOptions = LayoutOptions.Center;
        searchContainer.Add(child);
        var header = new Grid
        {
            Padding = new Thickness(8, 12), ColumnSpacing = 4,
            ColumnDefinitions = [new(48), new(GridLength.Star), new(48)]
        };
        header.Add(menu, 0);
        header.Add(searchContainer, 1);
        header.Add(refresh, 2);
        var footer = new Grid { Padding = new Thickness(16, 8), ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        progress.WidthRequest = progress.HeightRequest = 24;
        footer.Add(progress, 0);
        footer.Add(status, 1);
        footer.Add(language, 2);
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

    internal ReaderPage Rebuild(Action openMenu)
    {
        var rebuilt = new ReaderPage(openMenu)
        {
            session = session,
            known = known,
            archive = archive,
            account = account,
            initialized = initialized,
            folderIndex = folderIndex
        };
        rebuilt.search.Text = search.Text;
        rebuilt.RestoreArchiveStatus();
        rebuilt.ApplySearch();
        rebuilt.UpdateContext();
        rebuilt.SetBusy(false);
        return rebuilt;
    }

    internal Task AccountSavedAsync(Login login) => RunAsync(() => ReloadAsync(login));
    internal Task SelectAccountAsync(Login login) => RunAsync(() => SelectAsync(login));
    internal Task AddAccountAsync() => OpenSignInAsync();
    internal AccountPickerPage CreateAccountPickerPage() => new(known, account, login => RunAsync(async () =>
    {
        await SelectAsync(login);
        await Navigation.PopAsync();
    }));

    private void RestoreArchiveStatus() => status.Text = account is null ? AppStrings.ReaderStatusNoAccounts :
        archive is null ? AppStrings.ReaderStatusNoArchive : ArchiveStatus(archive);

    private void CancelRefreshStatus()
    {
        refreshStatusTimer.Stop();
        refreshStatusTimeout.Cancel();
    }
    private static string ArchiveStatus(Archive archive)
    {
        var synced = DateTimeOffset.Parse(archive.SyncedAt).ToLocalTime();
        var now = DateTimeOffset.Now;
        var days = (now.Date - synced.Date).Days;
        var date = days switch
        {
            0 => SharedStrings.GroupToday,
            1 => SharedStrings.GroupYesterday,
            >= 2 and <= 4 => Localized.Format(AppStrings.ReaderStatusDaysAgoFew, days),
            >= 5 and <= 9 => Localized.Format(AppStrings.ReaderStatusDaysAgoMany, days),
            _ => synced.ToString(synced.Year == now.Year ? SharedStrings.DateShortFormat :
                SharedStrings.DateShortYearFormat, LanguageSettings.Culture)
        };
        return Localized.Format(AppStrings.ReaderStatusArchiveReady,
            date + " " + synced.ToString(SharedStrings.DateTimeFormat, LanguageSettings.Culture));
    }

    internal SettingsPage CreateSettingsPage() => new();
    internal Task OpenSettingsAsync() => Navigation.PushAsync(CreateSettingsPage());

    internal void SelectFolder(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FolderKeys.Length);
        folderIndex = index;
        ApplySearch();
    }

    private void UpdateContext()
    {
        var label = account is null ? null : AccountLabelConverter.Label(account);
        child.Text = label is null ? "" : AvatarPresentation.Initials(label);
        child.ImageSource = label is null ? ImageSource.FromFile("child.png") : null;
        child.FontSize = AvatarPresentation.UseLargeInitial(account, known) ? 24 : 16;
        ReaderTheme.SetAvatarColors(child, account);
        SemanticProperties.SetDescription(child, label is null ? AppStrings.ReaderAccountPickerTitle :
            Localized.Format(AppStrings.ReaderSelectedChild, label));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initialized || busy) return;
        await RunAsync(InitializeAsync);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (refreshStatusTimeout.Pending) RestoreArchiveStatus();
        CancelRefreshStatus();
    }

    private async Task InitializeAsync()
    {
        session = await ReaderSession.OpenAsync();
        initialized = true;
        await ReloadAsync();
    }

    private void SetBusy(bool value)
    {
        if (value) CancelRefreshStatus();
        busy = value;
        progress.IsRunning = value;
        progress.IsVisible = value;
        menu.IsEnabled = !value;
        language.IsEnabled = !value;
        child.IsEnabled = !value && known.Count > 0;
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
            ArchiveStatus(loaded);
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
            items = items.Where(item => $"{item.Title}\n{item.SenderName}\n{item.Text}\n{item.SentDate}\n{MessageInsights.DateSearchText(item.Date)}"
                .Contains(query, StringComparison.CurrentCultureIgnoreCase));
        messages.ItemsSource = items.OrderByDescending(item => item.Date)
            .Select((message, index) => new MessageListItem(message, index % 2 == 1)).ToList();
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

    internal async Task AccountActionsAsync()
    {
        if (session is null || busy) return;
        await Navigation.PushAsync(CreateAccountsPage());
    }

    internal AccountsPage CreateAccountsPage() => new(
        session ?? throw new ObkException(AppStrings.ErrorArchiveNotOpen),
        OpenSignInAsync, () => RunAsync(() => ReloadAsync()));

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
        refreshStatusTimeout.Start();
        refreshStatusTimer.Start();
        if (failures.Count > 0 || missing.Count > 0)
            await DisplayAlertAsync(AppStrings.ReaderRefreshAlertTitle, status.Text, AppStrings.AlertOkButton);
        if (missing.Count > 0)
        {
            var login = missing.FirstOrDefault(item => item.Url == account?.Url && item.Username == account.Username) ?? missing[0];
            await Navigation.PushAsync(new SignInPage(session, login, savedLogin => RunAsync(() => ReloadAsync(savedLogin))));
        }
    }

    internal static class AccountLabelConverter
    {
        public static string Label(Login login) => !string.IsNullOrWhiteSpace(login.ChildName) ? login.ChildName :
            (login.SchoolName is { } school ? Localized.Format(AppStrings.AccountSchoolAndUsername, school, login.Username) : login.Username);
    }

    private sealed class DateConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is DateTimeOffset date ? ShortDate(date.ToLocalTime()) : "";
        private static string ShortDate(DateTimeOffset date)
        {
            var now = DateTimeOffset.Now;
            return date.ToString(date.Date == now.Date ? SharedStrings.DateTimeFormat :
                date.Year == now.Year ? SharedStrings.DateShortFormat : SharedStrings.DateShortYearFormat,
                LanguageSettings.Culture);
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
