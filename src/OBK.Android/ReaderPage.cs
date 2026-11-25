using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class ReaderPage : ContentPage
{
    private readonly ImageButton menu = ReaderTheme.Icon("menu", AppStrings.ReaderNavigationMenu);
    private readonly ImageButton closeSearch = ReaderTheme.Icon("back", AppStrings.ReaderCloseSearch);
    private readonly Button child = ReaderTheme.Primary(new Button
    {
        WidthRequest = 56, HeightRequest = 56, Padding = 0, CornerRadius = 28,
        FontSize = 16, FontAttributes = FontAttributes.Bold
    });
    private readonly Grid childArea = new()
    {
        WidthRequest = 56, HeightRequest = 56, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center
    };
    private View? groupAvatar;
    private readonly Entry search = new()
    {
        Placeholder = AppStrings.ReaderSearchPlaceholder, ReturnType = ReturnType.Done,
        ClearButtonVisibility = ClearButtonVisibility.Never, BackgroundColor = Colors.Transparent,
        VerticalOptions = LayoutOptions.Center
    };
    private readonly Image searchGlyph = new() { WidthRequest = 20, HeightRequest = 20, IsVisible = false, InputTransparent = true };
    private readonly ImageButton searchIcon = ReaderTheme.Icon("search", AppStrings.ReaderSearchPlaceholder);
    private readonly ImageButton clearSearch = ReaderTheme.Icon("close", AppStrings.ReaderClearSearch);
    private readonly ImageButton arrange = ReaderTheme.Icon("sliders", AppStrings.ReaderArrangeOptions);
    private readonly Grid searchContent = new() { ColumnDefinitions = [new(56), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)] };
    private readonly Border searchPanel;
    private readonly Grid searchContainer = new() { MinimumHeightRequest = 56, IsClippedToBounds = false };
    private readonly VerticalStackLayout arrangeOptions = new() { Spacing = 4 };
    private readonly Border arrangePanel;
    private readonly Label summary = ReaderTheme.Metadata(new Label { FontSize = 12, Padding = new Thickness(12, 6) });
    private readonly VerticalStackLayout searchTools = new() { IsVisible = false };
    private readonly IDispatcherTimer searchTimer;
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
    private IReadOnlyList<AccountArchive> archives = [];
    private Login? account;
    private bool allChildren = ReaderPreferences.AllChildren;
    private bool busy;
    private bool searching;
    private bool optionsVisible;
    private int folderIndex;
    private bool initialized;
    internal bool Busy => busy;
    internal bool AccountsAvailable => session is not null && !busy;
    internal int FolderIndex => folderIndex;
    internal IReadOnlyList<string> FolderLabels => folderLabels;
    internal IReadOnlyList<Login> KnownAccounts => known;
    internal bool ShowingAllChildren => allChildren && known.Count > 1;
    internal Login? ActiveAccount => ShowingAllChildren ? null : account;
    private readonly RefreshStatusTimeout refreshStatusTimeout = new(TimeProvider.System);
    private readonly IDispatcherTimer refreshStatusTimer;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private static readonly string[] FolderKeys = ["received", "noticeboard", "sent", "excuses"];
    internal static readonly string[] FolderIcons = ["inbox", "noticeboard", "sent", "excuses"];
    private readonly string[] folderLabels = [SharedStrings.FolderInbox, SharedStrings.FolderNoticeboard, SharedStrings.FolderSent, SharedStrings.FolderExcuses];

    private sealed record MessageEntry(Message Message, Login Owner, string Folder, string Person, string SearchText, HashSet<string> Opened)
    {
        public bool IsNew => Folder is "received" or "noticeboard" && !Opened.Contains(Message.Id);
    }
    private sealed record AccountArchive(Login Login, Archive? Archive, IReadOnlyList<MessageEntry> Entries);
    private sealed record GroupHeader(string Title);
    private sealed record MessageListItem(MessageEntry Entry, bool IsAlternate, bool ShowOwner, string OwnerLabel);

    public ReaderPage(Action openMenu)
    {
        Title = "OBK";
        language = LanguageToolbar.CreateButton(this);
        NavigationPage.SetHasNavigationBar(this, false);
        menu.Clicked += (_, _) => openMenu();
        closeSearch.Clicked += (_, _) => ExitSearch();
        child.Clicked += async (_, _) => await Navigation.PushAsync(CreateAccountPickerPage());
        refreshStatusTimer = Dispatcher.CreateTimer();
        refreshStatusTimer.Interval = TimeSpan.FromSeconds(1);
        refreshStatusTimer.Tick += (_, _) =>
        {
            if (busy || !refreshStatusTimeout.TryExpire()) return;
            refreshStatusTimer.Stop();
            RestoreArchiveStatus();
        };
        searchTimer = Dispatcher.CreateTimer();
        searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        searchTimer.IsRepeating = false;
        searchTimer.Tick += (_, _) => ApplyList();
        search.TextChanged += (_, _) =>
        {
            clearSearch.IsVisible = searching && !string.IsNullOrEmpty(search.Text);
            searchTimer.Stop();
            if (string.IsNullOrWhiteSpace(search.Text)) ApplyList();
            else searchTimer.Start();
        };
        search.Focused += (_, _) => EnterSearch();
        search.Completed += (_, _) => search.Unfocus();
        search.HandlerChanged += (_, _) =>
        {
            if (search.Handler?.PlatformView is global::Android.Widget.EditText native) native.Background = null;
        };
        searchIcon.Clicked += (_, _) => search.Focus();
        clearSearch.Clicked += (_, _) =>
        {
            search.Text = "";
            search.Focus();
        };
        arrange.Clicked += (_, _) =>
        {
            optionsVisible = !optionsVisible;
            RenderArrangeOptions();
            UpdateSearchLayout();
        };
        ReaderTheme.SetIcon(searchGlyph, "search_muted");
        refresh.Clicked += async (_, _) => await RunAsync(session is null ? InitializeAsync : RefreshAllAsync);
        messages.ItemTemplate = new RowSelector(CreateHeaderTemplate(), CreateMessageTemplate());
        addChild.Clicked += async (_, _) => await OpenSignInAsync();
        messages.EmptyView = new VerticalStackLayout { Padding = 24, Spacing = 12, Children = { empty, addChild } };
        messages.SelectionChanged += async (_, args) =>
        {
            var selected = args.CurrentSelection.FirstOrDefault();
            if (selected is null) return;
            messages.SelectedItem = null;
            if (selected is MessageListItem entry && session is not null)
                await RunAsync(() => OpenMessageAsync(entry.Entry));
        };
        searchContent.Add(searchGlyph, 0);
        searchGlyph.HorizontalOptions = LayoutOptions.End;
        searchContent.Add(search, 1);
        searchContent.Add(clearSearch, 2);
        searchContent.Add(searchIcon, 3);
        searchContent.Add(arrange, 3);
        clearSearch.IsVisible = arrange.IsVisible = false;
        searchPanel = ReaderTheme.Panel(searchContent, 0);
        searchPanel.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 };
        searchPanel.HeightRequest = 48;
        searchPanel.VerticalOptions = LayoutOptions.Center;
        var focusSearch = new TapGestureRecognizer();
        focusSearch.Tapped += (_, _) => search.Focus();
        searchPanel.GestureRecognizers.Add(focusSearch);
        searchContainer.Add(searchPanel);
        childArea.Add(child);
        searchContainer.Add(childArea);
        var header = new Grid
        {
            Padding = new Thickness(8, 12), ColumnSpacing = 4,
            ColumnDefinitions = [new(48), new(GridLength.Star), new(48)]
        };
        header.Add(menu, 0);
        header.Add(closeSearch, 0);
        closeSearch.IsVisible = false;
        header.Add(searchContainer, 1);
        header.Add(refresh, 2);
        arrangePanel = ReaderTheme.Panel(arrangeOptions, new Thickness(16, 12));
        arrangePanel.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 };
        arrangePanel.Margin = new Thickness(12, 0, 12, 4);
        arrangePanel.IsVisible = false;
        searchTools.Add(arrangePanel);
        searchTools.Add(summary);
        var footer = new Grid { Padding = new Thickness(16, 8), ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        progress.WidthRequest = progress.HeightRequest = 24;
        footer.Add(progress, 0);
        footer.Add(status, 1);
        footer.Add(language, 2);
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(header);
        layout.Add(searchTools, 0, 1);
        layout.Add(messages, 0, 2);
        layout.Add(footer, 0, 3);
        messages.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Surface, ReaderTheme.DarkSurface);
        Content = layout;
        ReaderTheme.Apply(this);
        ReaderTheme.Apply(empty);
        UpdateContext();
        SetBusy(false);
    }

    private DataTemplate CreateHeaderTemplate() => new(() =>
    {
        var label = ReaderTheme.Metadata(new Label { FontSize = 12, FontAttributes = FontAttributes.Bold, Padding = new Thickness(12, 6) });
        label.SetBinding(Label.TextProperty, nameof(GroupHeader.Title));
        var grid = new Grid { Children = { label } };
        grid.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F3F3F3"), Color.FromArgb("#202020"));
        SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level2);
        return grid;
    });

    private DataTemplate CreateMessageTemplate() => new(() =>
    {
        var letter = new Label
        {
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center
        };
        var avatar = new Border
        {
            Content = letter, WidthRequest = 40, HeightRequest = 40, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 }
        };
        var title = new Label { MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
        title.SetDynamicResource(Label.FontSizeProperty, "ListSubjectSize");
        var sender = ReaderTheme.Metadata(new Label { MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation }, "ListSenderSize");
        var date = ReaderTheme.Metadata(new Label { MaxLines = 1, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.End }, "ListDateSize");
        var dot = new Border
        {
            WidthRequest = 10, HeightRequest = 10, StrokeThickness = 0, HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 }
        };
        dot.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8,
            MinimumHeightRequest = 48
        };
        row.SetDynamicResource(Grid.PaddingProperty, "ListRowPadding");
        row.SetDynamicResource(Grid.RowSpacingProperty, "ListLineGap");
        row.Add(avatar, 0, 0);
        Grid.SetRowSpan(avatar, 2);
        row.Add(title, 1, 0);
        row.Add(date, 2, 0);
        row.Add(sender, 1, 1);
        row.Add(dot, 2, 1);
        var item = new Grid { RowDefinitions = [new(GridLength.Auto), new(1)], RowSpacing = 0 };
        item.Add(row);
        item.Add(new BoxView(), 0, 1);
        item.BindingContextChanged += (_, _) =>
        {
            if (item.BindingContext is not MessageListItem entry) return;
            var message = entry.Entry.Message;
            var isNew = entry.Entry.IsNew;
            avatar.IsVisible = entry.ShowOwner;
            if (entry.ShowOwner) ReaderTheme.StyleAvatar(avatar, letter, entry.Entry.Owner, known, 20);
            title.Text = message.Title;
            title.FontAttributes = isNew ? FontAttributes.Bold : FontAttributes.None;
            title.SetAppThemeColor(Label.TextColorProperty, isNew ? ReaderTheme.Foreground : ReaderTheme.OpenedForeground,
                isNew ? ReaderTheme.DarkForeground : ReaderTheme.DarkOpenedForeground);
            sender.Text = entry.ShowOwner ? $"{entry.OwnerLabel} · {entry.Entry.Person}" : entry.Entry.Person;
            date.Text = ShortDate(message.Date.ToLocalTime());
            date.FontAttributes = isNew ? FontAttributes.Bold : FontAttributes.None;
            if (isNew) date.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
            else ReaderTheme.Metadata(date, "ListDateSize");
            dot.IsVisible = isNew;
            SemanticProperties.SetDescription(item, isNew ? Localized.Format(AppStrings.ReaderNewMessage, message.Title) : null);
            item.SetAppThemeColor(BackgroundColorProperty,
                entry.IsAlternate ? ReaderTheme.AlternateSurface : ReaderTheme.Surface,
                entry.IsAlternate ? ReaderTheme.DarkAlternateSurface : ReaderTheme.DarkSurface);
        };
        return ReaderTheme.Apply(item);
    });

    internal ReaderPage Rebuild(Action openMenu)
    {
        var rebuilt = new ReaderPage(openMenu)
        {
            session = session,
            known = known,
            archives = archives,
            account = account,
            allChildren = allChildren,
            initialized = initialized,
            folderIndex = folderIndex
        };
        if (!string.IsNullOrEmpty(search.Text))
        {
            rebuilt.search.Text = search.Text;
            rebuilt.EnterSearch();
        }
        rebuilt.RestoreArchiveStatus();
        rebuilt.ApplyList();
        rebuilt.UpdateContext();
        rebuilt.SetBusy(false);
        return rebuilt;
    }

    internal Task AccountSavedAsync(Login login) => RunAsync(() => ReloadAsync(login));
    internal Task SelectAccountAsync(Login login) => RunAsync(() => SelectAsync(login));
    internal Task SelectAllChildrenAsync() => RunAsync(() => { SelectAllChildren(); return Task.CompletedTask; });
    internal Task AddAccountAsync() => OpenSignInAsync();
    internal AccountPickerPage CreateAccountPickerPage() => new(known, ActiveAccount, ShowingAllChildren, login => RunAsync(async () =>
    {
        if (login is null) SelectAllChildren();
        else await SelectAsync(login);
        await Navigation.PopAsync();
    }));

    internal int NewCount(Login login) => archives.Where(item => SameAccount(item.Login, login))
        .Sum(item => item.Entries.Count(entry => entry.IsNew));
    internal int NewCountAll => archives.Sum(item => item.Entries.Count(entry => entry.IsNew));
    internal int FolderNewCount(int index) => ScopeEntries().Count(entry => entry.Folder == FolderKeys[index] && entry.IsNew);

    private static bool SameAccount(Login? left, Login? right) =>
        left is not null && right is not null && left.Url == right.Url && left.Username == right.Username;

    private IEnumerable<AccountArchive> ScopeArchives() => ShowingAllChildren ? archives :
        archives.Where(item => SameAccount(item.Login, account));
    private IEnumerable<MessageEntry> ScopeEntries() => ScopeArchives().SelectMany(item => item.Entries);

    private void RestoreArchiveStatus()
    {
        var loaded = ScopeArchives().Select(item => item.Archive).OfType<Archive>().ToList();
        status.Text = account is null ? AppStrings.ReaderStatusNoAccounts :
            loaded.Count == 0 ? AppStrings.ReaderStatusNoArchive :
            ArchiveStatus(loaded.MinBy(item => DateTimeOffset.Parse(item.SyncedAt))!);
    }

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
        ApplyList();
    }

    private void UpdateContext()
    {
        if (groupAvatar is not null) childArea.Remove(groupAvatar);
        groupAvatar = null;
        if (ShowingAllChildren)
        {
            child.Text = "";
            child.ImageSource = null;
            child.SetAppThemeColor(Button.BackgroundColorProperty, ReaderTheme.Selection, ReaderTheme.DarkSelection);
            groupAvatar = ReaderTheme.GroupAvatar(known, 56);
            childArea.Add(groupAvatar);
            SemanticProperties.SetDescription(child, AppStrings.ReaderSelectedAllChildren);
            return;
        }
        var label = account is null ? null : AccountLabelConverter.Label(account);
        child.Text = label is null ? "" : AvatarPresentation.Initials(label);
        child.ImageSource = label is null ? ImageSource.FromFile("child.png") : null;
        child.FontSize = AvatarPresentation.UseLargeInitial(account, known) ? 24 : 16;
        ReaderTheme.SetAvatarColors(child, account);
        SemanticProperties.SetDescription(child, label is null ? AppStrings.ReaderAccountPickerTitle :
            Localized.Format(AppStrings.ReaderSelectedChild, label));
    }

    private void EnterSearch()
    {
        if (searching) return;
        searching = true;
        UpdateSearchLayout();
        ApplyList();
    }

    private void ExitSearch()
    {
        if (!searching) return;
        searching = optionsVisible = false;
        search.Unfocus();
        searchTimer.Stop();
        search.Text = "";
        UpdateSearchLayout();
        ApplyList();
    }

    private void UpdateSearchLayout()
    {
        menu.IsVisible = refresh.IsVisible = childArea.IsVisible = searchIcon.IsVisible = !searching;
        closeSearch.IsVisible = searchGlyph.IsVisible = arrange.IsVisible = searchTools.IsVisible = searching;
        clearSearch.IsVisible = searching && !string.IsNullOrEmpty(search.Text);
        arrangePanel.IsVisible = searching && optionsVisible;
        Grid.SetColumnSpan(searchContainer, searching ? 2 : 1);
        searchContent.ColumnDefinitions[0].Width = searching ? 40 : 56;
        search.Margin = new Thickness(searching ? 8 : 0, 0, 0, 0);
        arrange.SetAppThemeColor(ImageButton.BackgroundColorProperty,
            optionsVisible ? ReaderTheme.Selection : Colors.Transparent, optionsVisible ? ReaderTheme.DarkSelection : Colors.Transparent);
        searchPanel.SetAppTheme<Brush>(Border.StrokeProperty,
            new SolidColorBrush(searching ? ReaderTheme.Accent : ReaderTheme.Stroke),
            new SolidColorBrush(searching ? ReaderTheme.DarkAccent : ReaderTheme.DarkStroke));
    }

    private ListGrouping EffectiveGrouping => ReaderPreferences.Grouping is ListGrouping.Child && !ShowingAllChildren
        ? ListGrouping.None : ReaderPreferences.Grouping;

    private void RenderArrangeOptions()
    {
        arrangeOptions.Children.Clear();
        var grouping = EffectiveGrouping;
        arrangeOptions.Add(ReaderTheme.Metadata(new Label { Text = AppStrings.ReaderGroupHeading, FontSize = 12, FontAttributes = FontAttributes.Bold }));
        var groups = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var (value, label) in new[]
        {
            (ListGrouping.None, AppStrings.ReaderOptionNone), (ListGrouping.Date, AppStrings.ReaderOptionDate),
            (ListGrouping.Child, AppStrings.ReaderOptionChild), (ListGrouping.Sender, AppStrings.ReaderOptionSender),
            (ListGrouping.Subject, AppStrings.ReaderOptionSubject)
        })
        {
            if (value is ListGrouping.Child && !ShowingAllChildren) continue;
            groups.Add(ReaderTheme.Chip(label, value == grouping, (_, _) =>
            {
                ReaderPreferences.Grouping = value;
                RenderArrangeOptions();
                ApplyList();
            }));
        }
        arrangeOptions.Add(groups);
        arrangeOptions.Add(ReaderTheme.Metadata(new Label { Text = AppStrings.ReaderSortHeading, FontSize = 12, FontAttributes = FontAttributes.Bold }));
        var sorts = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var (value, label) in new[]
        {
            (MessageSort.Date, AppStrings.ReaderOptionDate), (MessageSort.Sender, AppStrings.ReaderOptionSender),
            (MessageSort.Title, AppStrings.ReaderOptionSubject)
        })
            sorts.Add(ReaderTheme.Chip(label, value == ReaderPreferences.Sort, (_, _) =>
            {
                ReaderPreferences.Sort = value;
                RenderArrangeOptions();
                ApplyList();
            }));
        arrangeOptions.Add(sorts);
        var hide = ReaderTheme.StartAlignedButton(new Button { Text = AppStrings.ReaderHideOptions, FontAttributes = FontAttributes.Bold, Padding = new Thickness(0, 8) });
        hide.Clicked += (_, _) =>
        {
            optionsVisible = false;
            UpdateSearchLayout();
        };
        arrangeOptions.Add(hide);
        ReaderTheme.Apply(arrangeOptions);
    }

    protected override bool OnBackButtonPressed()
    {
        if (!searching) return base.OnBackButtonPressed();
        ExitSearch();
        return true;
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
        var store = session.Store;
        var (logins, last) = await Task.Run(() => (store.KnownLogins(), store.LastAccount()));
        var year = SchoolYear.Current();
        archives = await Task.Run(() => logins.Select(login =>
        {
            var archive = store.LoadArchive(login.Url, login.Username, year);
            if (archive is null) return new AccountArchive(login, null, []);
            var opened = store.OpenedMessages(login.Url, login.Username,
                archive.Folders.Values.SelectMany(folder => folder).Select(message => message.Id));
            return new AccountArchive(login, archive, Entries(login, archive, opened));
        }).ToList());
        known = logins;
        var selected = preferred ?? account ?? last;
        account = known.FirstOrDefault(item => SameAccount(item, selected)) ?? known.FirstOrDefault();
        if (preferred is not null && account is { } chosen)
            await Task.Run(() => store.SaveAccount(chosen.Url, chosen.Username, chosen.ChildName, chosen.SchoolName));
        UpdateContext();
        RestoreArchiveStatus();
        ApplyList();
    }

    private static List<MessageEntry> Entries(Login login, Archive archive, HashSet<string> opened)
    {
        var entries = new List<MessageEntry>();
        void Add(string key, string source, IEnumerable<Message> items)
        {
            foreach (var message in items)
                entries.Add(new MessageEntry(message, login, key, Person(message, source),
                    MessageInsights.SearchText(message, source), opened));
        }
        Add("received", "received", archive.Folders["received"]);
        Add("noticeboard", "noticeboard", archive.Folders["noticeboard"]);
        Add("sent", "sent", archive.Folders["sent"].Where(item => !MessageInsights.IsExcuse(item)));
        Add("excuses", "sent", archive.Folders["sent"].Where(MessageInsights.IsExcuse));
        return entries;
    }

    private static string Person(Message message, string folder) =>
        string.Join(' ', MessageInsights.Counterpart(message, folder).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task SelectAsync(Login login)
    {
        if (session is null) throw new ObkException(AppStrings.ErrorArchiveNotOpen);
        var store = session.Store;
        await Task.Run(() => store.SaveAccount(login.Url, login.Username, login.ChildName, login.SchoolName));
        allChildren = ReaderPreferences.AllChildren = false;
        account = known.FirstOrDefault(item => SameAccount(item, login)) ?? login;
        UpdateContext();
        RestoreArchiveStatus();
        RenderArrangeOptions();
        ApplyList();
    }

    private void SelectAllChildren()
    {
        allChildren = ReaderPreferences.AllChildren = true;
        UpdateContext();
        RestoreArchiveStatus();
        RenderArrangeOptions();
        ApplyList();
    }

    private async Task OpenMessageAsync(MessageEntry entry)
    {
        if (session is null) return;
        if (entry.IsNew)
        {
            var store = session.Store;
            await Task.Run(() => store.MarkOpened(entry.Owner.Url, entry.Owner.Username, entry.Message.Id));
            entry.Opened.Add(entry.Message.Id);
            ApplyList();
        }
        await Navigation.PushAsync(new MessagePage(session, entry.Owner, entry.Message));
    }

    private void ApplyList()
    {
        var key = FolderKeys[folderIndex];
        var query = search.Text?.Trim() ?? "";
        var entries = ScopeEntries().Where(entry => entry.Folder == key);
        if (query.Length > 0) entries = entries.Where(entry => MessageInsights.Matches(entry.SearchText, query));
        var grouping = EffectiveGrouping;
        var sort = ReaderPreferences.Sort;
        var today = DateTime.Today;
        var showOwner = ShowingAllChildren;
        IEnumerable<(MessageEntry Item, string Group)> arranged = grouping is ListGrouping.Child
            ? known.SelectMany(login => MessageInsights.Arrange(entries.Where(entry => SameAccount(entry.Owner, login)),
                entry => entry.Message, entry => entry.Person, sort, MessageGrouping.None, today)
                .Select(row => (row.Item, AccountLabelConverter.Label(login))))
            : MessageInsights.Arrange(entries, entry => entry.Message, entry => entry.Person, sort, grouping switch
            {
                ListGrouping.Date => MessageGrouping.Date,
                ListGrouping.Sender => MessageGrouping.Sender,
                ListGrouping.Subject => MessageGrouping.Title,
                _ => MessageGrouping.None
            }, today);
        var items = new List<object>();
        string? current = null;
        var index = 0;
        var count = 0;
        foreach (var (entry, group) in arranged)
        {
            if (grouping is not ListGrouping.None && group != current)
            {
                items.Add(new GroupHeader(group.Length > 0 ? group : "—"));
                current = group;
                index = 0;
            }
            items.Add(new MessageListItem(entry, index++ % 2 == 1, showOwner, AccountLabelConverter.Label(entry.Owner)));
            count++;
        }
        messages.ItemsSource = items;
        summary.Text = Localized.Format(AppStrings.ReaderSearchSummary, count, folderLabels[folderIndex],
            showOwner ? AppStrings.ReaderAllChildren : account is null ? "" : AccountLabelConverter.Label(account));
        addChild.IsVisible = session is not null && known.Count == 0;
        empty.Text = query.Length > 0 ? AppStrings.ReaderEmptySearch :
            !ScopeArchives().Any(item => item.Archive is not null) ? AppStrings.ReaderEmptyNoArchive : AppStrings.ReaderEmptyFolder;
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
        var summaryParts = new List<string> { Localized.Format(AppStrings.ReaderRefreshSummary, completed, known.Count) };
        if (missing.Count > 0)
            summaryParts.Add(Localized.Format(AppStrings.ReaderRefreshSignInNeeded, string.Join(", ", missing.Select(AccountLabelConverter.Label))));
        if (failures.Count > 0)
            summaryParts.Add(Localized.Format(AppStrings.ReaderRefreshFailed, string.Join("; ", failures)));
        status.Text = string.Join(" ", summaryParts);
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

    private static string ShortDate(DateTimeOffset date)
    {
        var now = DateTimeOffset.Now;
        return date.ToString(date.Date == now.Date ? SharedStrings.DateTimeFormat :
            date.Year == now.Year ? SharedStrings.DateShortFormat : SharedStrings.DateShortYearFormat,
            LanguageSettings.Culture);
    }

    private sealed class RowSelector(DataTemplate header, DataTemplate message) : DataTemplateSelector
    {
        protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
            item is GroupHeader ? header : message;
    }
}
