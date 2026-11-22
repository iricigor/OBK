using OBK.AndroidApp.Localization;

namespace OBK.AndroidApp;

internal sealed class SettingsPage : ContentPage
{
    private static readonly AppTheme[] Themes = [AppTheme.Unspecified, AppTheme.Light, AppTheme.Dark];
    private readonly Func<bool> accountsAvailable;
    private readonly Func<string?> currentAccount;
    private readonly Button accounts = ReaderTheme.Subtle(new Button { Text = AppStrings.SettingsManageAccounts });
    private readonly Label accountName = ReaderTheme.Metadata(new Label());

    public SettingsPage(Func<Task> accountActions, Func<bool> accountsAvailable, Func<string?> currentAccount)
    {
        this.accountsAvailable = accountsAvailable;
        this.currentAccount = currentAccount;
        LanguageToolbar.Add(this);
        Title = AppStrings.SettingsPageTitle;
        var appearance = new Picker
        {
            Title = AppStrings.SettingsAppearance,
            ItemsSource = new[] { AppStrings.SettingsThemeSystem, AppStrings.SettingsThemeLight, AppStrings.SettingsThemeDark },
            SelectedIndex = Array.IndexOf(Themes, Application.Current!.UserAppTheme)
        };
        SemanticProperties.SetDescription(appearance, AppStrings.SettingsAppearance);
        appearance.SelectedIndexChanged += (_, _) =>
        {
            if (appearance.SelectedIndex < 0) return;
            var theme = Themes[appearance.SelectedIndex];
            Preferences.Default.Set(App.AppearancePreference, theme.ToString());
            Application.Current!.UserAppTheme = theme;
        };
        accounts.Clicked += async (_, _) => await accountActions();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 12,
                Children =
                {
                    new Label { Text = AppStrings.SettingsAppearance, FontSize = 18, FontAttributes = FontAttributes.Bold },
                    ReaderTheme.Panel(appearance, new Thickness(8, 0)),
                    ReaderTheme.Metadata(new Label { Text = AppStrings.SettingsThemeExplanation }),
                    new Label { Text = AppStrings.ReaderAccountsSheetTitle, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 20, 0, 0) },
                    accountName, accounts
                }
            }
        };
        ReaderTheme.Apply(this);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        accounts.IsEnabled = accountsAvailable();
        accountName.Text = currentAccount();
        accountName.IsVisible = accountName.Text is not null;
    }
}
