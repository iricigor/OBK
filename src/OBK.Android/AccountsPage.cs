using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class AccountsPage : ContentPage
{
    private readonly ReaderSession session;
    private readonly Func<Login?, Task> signIn;
    private readonly Func<Task> accountsChanged;
    private readonly VerticalStackLayout accounts = new() { Spacing = 16 };
    private readonly ActivityIndicator progress = new() { IsVisible = false };
    private readonly List<Button> actions = [];
    private readonly Button add = ReaderTheme.Primary(new Button { Text = AppStrings.ReaderAccountsSheetAddChild });
    internal bool Busy { get; private set; }

    public AccountsPage(ReaderSession session, Func<Login?, Task> signIn, Func<Task> accountsChanged)
    {
        this.session = session;
        this.signIn = signIn;
        this.accountsChanged = accountsChanged;
        Title = AppStrings.SettingsManageAccounts;
        LanguageToolbar.Add(this);
        add.Clicked += async (_, _) => await RunAsync(() => signIn(null));
        var body = new VerticalStackLayout
        {
            Padding = 20, Spacing = 20,
            Children = { ReaderTheme.Metadata(new Label { Text = AppStrings.AccountsSavedHere }), progress, accounts }
        };
        var layout = new Grid { RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(new ScrollView { Content = body });
        layout.Add(new VerticalStackLayout { Padding = new Thickness(20, 12), Children = { add } }, 0, 1);
        Content = layout;
        ReaderTheme.Apply(this);
        this.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Surface, ReaderTheme.DarkSurface);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var (known, active) = await Task.Run(() => (session.Store.KnownLogins(), session.Store.LastAccount()));
        accounts.Children.Clear();
        actions.Clear();
        foreach (var login in known.OrderByDescending(login => active?.Url == login.Url && active.Username == login.Username))
        {
            var name = ReaderPage.AccountLabelConverter.Label(login);
            var title = new Label { Text = name, FontSize = 18, FontAttributes = FontAttributes.Bold };
            var details = new VerticalStackLayout { Spacing = 4 };
            var titleRow = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
            titleRow.Add(title);
            if (active?.Url == login.Url && active.Username == login.Username)
            {
                var current = new Label { Text = AppStrings.AccountsActive, FontSize = 13, VerticalOptions = LayoutOptions.Center };
                current.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
                titleRow.Add(current, 1);
            }
            details.Add(titleRow);
            if (!string.IsNullOrWhiteSpace(login.SchoolName))
                details.Add(ReaderTheme.Metadata(new Label { Text = login.SchoolName }));
            details.Add(ReaderTheme.Metadata(new Label { Text = login.Username }));
            var saved = ReaderTheme.Metadata(new Label
            {
                Text = login.Password is null ? AppStrings.AccountsPasswordNotSaved : AppStrings.AccountsPasswordSaved
            });
            if (login.Password is not null)
                saved.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.StatusGood, ReaderTheme.DarkStatusGood);
            details.Add(saved);
            var buttons = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
            var open = ReaderTheme.StartAlignedButton(new Button { Text = AppStrings.AccountsSignIn, LineBreakMode = LineBreakMode.WordWrap });
            open.Clicked += async (_, _) => await RunAsync(() => signIn(login));
            actions.Add(open);
            buttons.Add(open);
            if (login.Password is not null)
            {
                var forget = ReaderTheme.StartAlignedButton(new Button { Text = AppStrings.AccountsForgetPassword, LineBreakMode = LineBreakMode.WordWrap });
                forget.SetAppThemeColor(Button.TextColorProperty, ReaderTheme.Danger, ReaderTheme.DarkDanger);
                forget.Clicked += async (_, _) =>
                {
                    if (Busy) return;
                    var confirmed = await DisplayAlertAsync(AppStrings.AccountsForgetPassword,
                        Localized.Format(AppStrings.AccountsForgetConfirmation, name),
                        AppStrings.AccountsForgetPassword, AppStrings.ReaderAccountsSheetCancel);
                    if (!confirmed) return;
                    await RunAsync(async () =>
                    {
                        await Task.Run(() => session.Store.ForgetPassword(login.Url, login.Username));
                        await accountsChanged();
                        await RefreshAsync();
                    });
                };
                actions.Add(forget);
                buttons.Add(forget, 1);
            }
            details.Add(buttons);
            var row = new Grid { ColumnDefinitions = [new(48), new(GridLength.Star)], ColumnSpacing = 16 };
            row.Add(ReaderTheme.AccountAvatar(login, known));
            row.Add(details, 1);
            accounts.Add(row);
            accounts.Add(new BoxView { HeightRequest = 1 });
        }
        ReaderTheme.Apply(accounts);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        add.IsEnabled = false;
        actions.ForEach(button => button.IsEnabled = false);
        progress.IsVisible = progress.IsRunning = true;
        try { await action(); }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            await DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
        }
        finally
        {
            Busy = false;
            add.IsEnabled = true;
            actions.ForEach(button => button.IsEnabled = true);
            progress.IsVisible = progress.IsRunning = false;
        }
    }
}
