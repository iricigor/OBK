using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class MarksPage : ContentPage
{
    private readonly ReaderSession session;
    private readonly ReaderPage reader;
    private readonly Entry search = new() { Placeholder = SharedStrings.MarksSearch, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private readonly CollectionView entries = new() { SelectionMode = SelectionMode.Single };
    private readonly Label status = ReaderTheme.Metadata(new Label { FontSize = 12 });
    private readonly Label notice = ReaderTheme.Metadata(new Label { FontSize = 12 });
    private readonly Label scopeLabel = ReaderTheme.Metadata(new Label { FontSize = 12 });
    private readonly Label empty = ReaderTheme.Metadata(new Label { Padding = 24 });
    private readonly Button child = ReaderTheme.Subtle(new Button { Text = AppStrings.ReaderDrawerChildren });
    private readonly ImageButton refresh = ReaderTheme.Icon("refresh", AppStrings.ReaderRefreshAll);
    private readonly ActivityIndicator progress = new() { IsVisible = false };
    private MarksCatalog catalog = new([]);
    private bool recent;
    internal bool Busy { get; private set; }
    internal MarksPage Rebuild(ReaderPage next) => new(session, next, recent, search.Text ?? "");

    internal MarksPage(ReaderSession session, ReaderPage reader, bool recent = false, string query = "")
    {
        this.session = session;
        this.reader = reader;
        this.recent = recent;
        Title = SharedStrings.MarksTitle;
        LanguageToolbar.Add(this);
        search.Text = query;
        search.TextChanged += (_, _) => ApplyList();
        child.Clicked += async (_, _) =>
        {
            if (Busy) return;
            await Navigation.PushAsync(new AccountPickerPage(reader.KnownAccounts, reader.ActiveAccount, reader.ShowingAllChildren,
                async login =>
                {
                    await reader.ChangeMarksScopeAsync(login);
                    await Navigation.PopAsync();
                }));
        };
        refresh.Clicked += async (_, _) => await PerformAsync(RefreshAsync);
        entries.ItemTemplate = new DataTemplate(CreateRow);
        entries.EmptyView = empty;
        entries.SelectionChanged += async (_, args) =>
        {
            if (Busy || args.CurrentSelection.FirstOrDefault() is not MarksEntry entry) return;
            entries.SelectedItem = null;
            await Navigation.PushAsync(new MarksDetailPage(session, entry));
        };
        var heading = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8, Padding = new Thickness(12, 4)
        };
        heading.Add(child);
        heading.Add(progress, 1);
        heading.Add(refresh, 2);
        var top = new VerticalStackLayout
        {
            Padding = new Thickness(14, 0, 14, 12), Spacing = 8,
            Children =
            {
                heading, scopeLabel, search,
                ReaderTheme.Segmented([SharedStrings.MarksSubjects, SharedStrings.MarksRecent], recent ? 1 : 0,
                    index => { this.recent = index == 1; ApplyList(); }),
                notice
            }
        };
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(top);
        layout.Add(entries, 0, 1);
        layout.Add(new VerticalStackLayout { Padding = new Thickness(14, 8), Children = { status } }, 0, 2);
        Content = layout;
        ReaderTheme.Apply(this);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!Busy) await LoadScopeAsync();
    }

    internal Task LoadScopeAsync() => PerformAsync(LoadAsync);

    private async Task LoadAsync()
    {
        catalog = await Task.Run(() => MarksCatalog.Read(session.Store, reader.MarksScope));
        child.Text = reader.ShowingAllChildren ? AppStrings.ReaderAllChildren :
            reader.ActiveAccount is { } account ? ReaderPage.AccountLabelConverter.Label(account) : AppStrings.ReaderDrawerChildren;
        scopeLabel.Text = SharedStrings.MarksCurrentPeriod;
        notice.Text = catalog.Notice;
        notice.IsVisible = notice.Text.Length > 0;
        ApplyList();
    }

    private void ApplyList()
    {
        entries.ItemsSource = recent ? catalog.Recent(search.Text ?? "") : catalog.Subjects(search.Text ?? "");
        empty.Text = !string.IsNullOrWhiteSpace(search.Text) ? SharedStrings.MarksNoMatches :
            catalog.Accounts.Count == 0 ? SharedStrings.MarksNoAccounts :
            catalog.Accounts.All(account => account.Archive is null) ? SharedStrings.MarksNoData : SharedStrings.MarksEmpty;
    }

    private View CreateRow()
    {
        var initial = new Label { FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
        var avatar = new Border
        {
            Content = initial, WidthRequest = 40, HeightRequest = 40, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 }
        };
        var title = new Label { MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
        title.SetDynamicResource(Label.FontSizeProperty, "ListSubjectSize");
        title.SetBinding(Label.TextProperty, nameof(MarksEntry.Title));
        var subtitle = ReaderTheme.Metadata(new Label { MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation }, "ListSenderSize");
        subtitle.SetBinding(Label.TextProperty, nameof(MarksEntry.RowSubtitle));
        var detail = ReaderTheme.Metadata(new Label { MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, MaximumWidthRequest = 130 }, "ListDateSize");
        detail.SetBinding(Label.TextProperty, nameof(MarksEntry.RowDetail));
        var fresh = new Border
        {
            WidthRequest = 10, HeightRequest = 10, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 }
        };
        fresh.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        fresh.SetBinding(IsVisibleProperty, nameof(MarksEntry.IsNew));
        var value = new Label { FontAttributes = FontAttributes.Bold, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, HorizontalOptions = LayoutOptions.End, MaximumWidthRequest = 90 };
        value.SetDynamicResource(Label.FontSizeProperty, "ListSubjectSize");
        value.SetBinding(Label.TextProperty, nameof(MarksEntry.Value));
        var indicators = new HorizontalStackLayout
        {
            Spacing = 4, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center, Children = { detail, fresh }
        };
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8, MinimumHeightRequest = 48
        };
        grid.SetDynamicResource(Grid.PaddingProperty, "ListRowPadding");
        grid.SetDynamicResource(Grid.RowSpacingProperty, "ListLineGap");
        grid.Add(avatar);
        Grid.SetRowSpan(avatar, 2);
        avatar.SetBinding(IsVisibleProperty, nameof(MarksEntry.ShowOwner));
        grid.Add(title, 1);
        grid.Add(value, 2);
        grid.Add(subtitle, 1, 1);
        grid.Add(indicators, 2, 1);
        var item = new Grid { RowDefinitions = [new(GridLength.Auto), new(1)], RowSpacing = 0 };
        item.Add(grid);
        item.Add(new BoxView(), 0, 1);
        item.BindingContextChanged += (_, _) =>
        {
            if (item.BindingContext is not MarksEntry entry) return;
            if (entry.ShowOwner) ReaderTheme.StyleAvatar(avatar, initial, entry.Owner, reader.KnownAccounts, 20);
            title.FontAttributes = entry.IsNew ? FontAttributes.Bold : FontAttributes.None;
            title.SetAppThemeColor(Label.TextColorProperty, entry.IsNew ? ReaderTheme.Foreground : ReaderTheme.OpenedForeground,
                entry.IsNew ? ReaderTheme.DarkForeground : ReaderTheme.DarkOpenedForeground);
            item.SetAppThemeColor(BackgroundColorProperty,
                entry.IsAlternate ? ReaderTheme.AlternateSurface : ReaderTheme.Surface,
                entry.IsAlternate ? ReaderTheme.DarkAlternateSurface : ReaderTheme.DarkSurface);
        };
        item.SetBinding(SemanticProperties.DescriptionProperty, nameof(MarksEntry.AccessibleName));
        return ReaderTheme.Apply(item);
    }

    private async Task RefreshAsync()
    {
        var saved = await Task.Run(session.Store.RememberedLogins);
        var targets = ReaderPreferences.AlwaysRefreshAllChildren ? reader.KnownAccounts : reader.MarksScope;
        var failures = new List<string>();
        var completed = 0;
        foreach (var owner in targets)
        {
            var label = ReaderPage.AccountLabelConverter.Label(owner);
            status.Text = Localized.Format(SharedStrings.MarksRefreshing, label);
            try
            {
                var password = saved.FirstOrDefault(login => login.Url == owner.Url && login.Username == owner.Username)?.Password;
                if (password is null)
                {
                    var prompt = new PasswordPage(label);
                    await Navigation.PushAsync(prompt);
                    password = await prompt.Result;
                }
                if (password is null)
                {
                    failures.Add(Localized.Format(SharedStrings.ErrorInContext, label, SharedStrings.ErrorMissingCredentials));
                    continue;
                }
                await session.RefreshMarksAsync(owner, password, text => status.Text = text);
                completed++;
            }
            catch (ObkException error) { failures.Add(Localized.Format(SharedStrings.ErrorInContext, label, error.Message)); }
        }
        await LoadAsync();
        status.Text = Localized.Format(SharedStrings.MarksRefreshSummary, completed, targets.Count) +
            (failures.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, failures) : "");
        if (failures.Count > 0)
            await DisplayAlertAsync(AppStrings.ReaderRefreshAlertTitle, status.Text, AppStrings.AlertOkButton);
    }

    private async Task PerformAsync(Func<Task> operation)
    {
        if (Busy) return;
        Busy = true;
        child.IsEnabled = refresh.IsEnabled = entries.IsEnabled = false;
        progress.IsRunning = progress.IsVisible = true;
        try { await operation(); }
        catch (ObkException error)
        {
            status.Text = error.Message;
            await DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
        }
        finally
        {
            Busy = false;
            child.IsEnabled = refresh.IsEnabled = entries.IsEnabled = true;
            progress.IsRunning = progress.IsVisible = false;
        }
    }

    private sealed class PasswordPage : ContentPage
    {
        private readonly TaskCompletionSource<string?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool finishing;
        public Task<string?> Result => result.Task;
        public PasswordPage(string label)
        {
            Title = SharedStrings.MarksTitle;
            var field = new Entry { IsPassword = true };
            var submit = ReaderTheme.Primary(new Button { Text = AppStrings.ReaderRefreshAll });
            var cancel = ReaderTheme.Subtle(new Button { Text = AppStrings.ReaderAccountsSheetCancel });
            async Task FinishAsync(string? password)
            {
                if (finishing) return;
                finishing = true;
                field.Text = "";
                submit.IsEnabled = cancel.IsEnabled = false;
                await Navigation.PopAsync();
                result.TrySetResult(password);
            }
            submit.Clicked += async (_, _) =>
            {
                if (string.IsNullOrEmpty(field.Text))
                {
                    await DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, SharedStrings.ErrorMissingCredentials, AppStrings.AlertOkButton);
                    return;
                }
                await FinishAsync(field.Text);
            };
            cancel.Clicked += async (_, _) => await FinishAsync(null);
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 16,
                Children = { new Label { Text = Localized.Format(SharedStrings.MarksPassword, label) }, field, submit, cancel }
            };
            ReaderTheme.Apply(this);
        }
        protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            if (!finishing) result.TrySetResult(null);
            base.OnNavigatedFrom(args);
        }
    }
}
