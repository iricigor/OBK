using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class SettingsPage : ContentPage
{
    private static readonly AppTheme[] Themes = [AppTheme.Unspecified, AppTheme.Light, AppTheme.Dark];
    private readonly Func<bool> accountsAvailable;
    private readonly Func<string?> currentAccount;
    private readonly Button accounts = ReaderTheme.Subtle(new Button { Text = AppStrings.SettingsManageAccounts });
    private readonly Label accountName = ReaderTheme.Metadata(new Label());
    private readonly Button checkUpdate = new() { Text = AppStrings.UpdateCheck };
    private readonly Button installUpdate = ReaderTheme.Primary(new Button { Text = AppStrings.UpdateDownload, IsVisible = false });
    private readonly Button cancelUpdate = ReaderTheme.Subtle(new Button { Text = AppStrings.ReaderAccountsSheetCancel, IsVisible = false });
    private readonly Label updateStatus = ReaderTheme.Metadata(new Label { Text = AppStrings.UpdateManualNotice });
    private readonly ActivityIndicator updateProgress = new() { IsVisible = false };
    private AvailableUpdate? availableUpdate;
    private CancellationTokenSource? updateCancellation;
    internal bool Busy => updateCancellation is not null;

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
        checkUpdate.Clicked += async (_, _) => await PerformUpdateAsync(async cancel =>
        {
            availableUpdate = null;
            installUpdate.IsVisible = false;
            SetUpdateStatus(AppStrings.UpdateChecking);
            var installed = AndroidUpdateInstaller.InstalledVersion;
            availableUpdate = await Task.Run(async () =>
            {
                using var service = CreateUpdateService();
                return await service.CheckAndroidAsync(installed, cancel);
            }, cancel);
            SetUpdateStatus(availableUpdate is null ? AppStrings.UpdateCurrent :
                Localized.Format(AppStrings.UpdateAvailable, UpdateService.Display(availableUpdate.Version)));
            installUpdate.Text = AppStrings.UpdateDownload;
            installUpdate.IsVisible = availableUpdate is not null;
        });
        installUpdate.Clicked += async (_, _) => await PerformUpdateAsync(async cancel =>
        {
            if (availableUpdate is not { } update) return;
            SetUpdateStatus(AppStrings.UpdateDownloading);
            var path = await Task.Run(async () =>
            {
                using var service = CreateUpdateService();
                return await service.DownloadAsync(update, cancel);
            }, cancel);
            cancel.ThrowIfCancellationRequested();
            AndroidUpdateInstaller.Validate(path, update);
            installUpdate.Text = AppStrings.UpdateInstall;
            SetUpdateStatus(AppStrings.UpdateReady);
            cancelUpdate.IsVisible = false;
            var installed = await AndroidUpdateInstaller.InstallAsync(path, update, () =>
                DisplayAlertAsync(AppStrings.UpdatePermissionTitle, AppStrings.UpdatePermissionExplanation,
                    AppStrings.UpdateOpenSettings, AppStrings.ReaderAccountsSheetCancel));
            SetUpdateStatus(installed ? AppStrings.UpdateCurrent : AppStrings.UpdateNotInstalled);
            installUpdate.IsVisible = !installed;
        });
        cancelUpdate.Clicked += (_, _) => updateCancellation?.Cancel();
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
                    accountName, accounts,
                    new Label { Text = AppStrings.UpdateHeading, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 20, 0, 0) },
                    ReaderTheme.Metadata(new Label { Text = Localized.Format(AppStrings.UpdateInstalledVersion, AppInfo.Current.VersionString) }),
                    updateStatus, updateProgress, checkUpdate, installUpdate, cancelUpdate
                }
            }
        };
        ReaderTheme.Apply(this);
    }

    private static UpdateService CreateUpdateService() => new(Path.Combine(FileSystem.CacheDirectory, "updates"));

    private void SetUpdateStatus(string text)
    {
        updateStatus.Text = text;
        SemanticScreenReader.Default.Announce(text);
    }

    private async Task PerformUpdateAsync(Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        using var cancellation = new CancellationTokenSource();
        updateCancellation = cancellation;
        checkUpdate.IsEnabled = installUpdate.IsEnabled = false;
        cancelUpdate.IsVisible = updateProgress.IsVisible = updateProgress.IsRunning = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { SetUpdateStatus(AppStrings.UpdateCancelled); }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            SetUpdateStatus(error.Message);
            await DisplayAlertAsync(AppStrings.UpdateFailedTitle, error.Message, AppStrings.AlertOkButton);
        }
        finally
        {
            updateCancellation = null;
            checkUpdate.IsEnabled = installUpdate.IsEnabled = true;
            cancelUpdate.IsVisible = updateProgress.IsVisible = updateProgress.IsRunning = false;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        updateCancellation?.Cancel();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        accounts.IsEnabled = accountsAvailable();
        accountName.Text = currentAccount();
        accountName.IsVisible = accountName.Text is not null;
    }
}
