using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class SettingsPage : ContentPage
{
    private static readonly AppTheme[] Themes = [AppTheme.Unspecified, AppTheme.Light, AppTheme.Dark];
    private readonly Button checkUpdate = new() { Text = AppStrings.UpdateCheck };
    private readonly Button installUpdate = ReaderTheme.Primary(new Button { Text = AppStrings.UpdateDownload, IsVisible = false });
    private readonly Button cancelUpdate = ReaderTheme.Subtle(new Button { Text = AppStrings.ReaderAccountsSheetCancel, IsVisible = false });
    private readonly Label updateStatus = ReaderTheme.Metadata(new Label { Text = AppStrings.UpdateManualNotice });
    private readonly ActivityIndicator updateProgress = new() { IsVisible = false };
    private AvailableUpdate? availableUpdate;
    private CancellationTokenSource? updateCancellation;
    internal bool Busy => updateCancellation is not null;

    public SettingsPage()
    {
        LanguageToolbar.Add(this);
        Title = AppStrings.SettingsPageTitle;
        var appearance = ReaderTheme.Segmented(
            [AppStrings.SettingsThemeSystem, AppStrings.SettingsThemeLight, AppStrings.SettingsThemeDark],
            Math.Max(0, Array.IndexOf(Themes, Application.Current!.UserAppTheme)), index =>
            {
                var theme = Themes[index];
                Preferences.Default.Set(App.AppearancePreference, theme.ToString());
                Application.Current!.UserAppTheme = theme;
            });
        var readingSize = ReaderTheme.Segmented(
            [AppStrings.SettingsReadingSizeSmall, AppStrings.SettingsReadingSizeMedium, AppStrings.SettingsReadingSizeLarge],
            Math.Max(0, Array.IndexOf(ReaderPreferences.ReadingSizes, ReaderPreferences.ReadingSize)),
            index => ReaderPreferences.SetReadingSize(ReaderPreferences.ReadingSizes[index]));
        var density = ReaderTheme.Segmented(
            [AppStrings.SettingsListDensityCompact, AppStrings.SettingsListDensityRelaxed],
            Math.Max(0, Array.IndexOf(ReaderPreferences.ListDensities, ReaderPreferences.ListDensity)),
            index => ReaderPreferences.SetListDensity(ReaderPreferences.ListDensities[index]));
        var repository = ReaderTheme.StartAlignedButton(new Button
        {
            Text = SharedStrings.RepositoryLink,
            LineBreakMode = LineBreakMode.WordWrap,
            MinimumHeightRequest = 48,
            ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Right, 12)
        });
        repository.SetAppTheme<ImageSource>(Button.ImageSourceProperty,
            ImageSource.FromFile("external.png"), ImageSource.FromFile("external_dark.png"));
        SemanticProperties.SetHint(repository, "https://github.com/iricigor/OBK");
        repository.Clicked += async (_, _) =>
        {
            repository.IsEnabled = false;
            try
            {
                await OpenRepositoryAsync();
            }
            catch (Exception error) when (error is ObkException or FeatureNotSupportedException)
            {
                await DisplayAlertAsync(SharedStrings.RepositoryLink,
                    error is FeatureNotSupportedException ? AppStrings.ErrorNoAppForLink : error.Message,
                    AppStrings.AlertOkButton);
            }
            finally { repository.IsEnabled = true; }
        };
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
        checkUpdate.CornerRadius = 20;
        checkUpdate.SetAppThemeColor(Button.BorderColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(12, 16), Spacing = 6,
                Children =
                {
                    Section(AppStrings.SettingsAppearance, appearance,
                        ReaderTheme.Metadata(new Label { Text = AppStrings.SettingsThemeExplanation, FontSize = 12 })),
                    Section(AppStrings.SettingsSectionReading,
                        RowTitle(AppStrings.SettingsReadingSize, AppStrings.SettingsReadingSizeDescription), readingSize,
                        new BoxView { HeightRequest = 1, Margin = new Thickness(0, 8) },
                        RowTitle(AppStrings.SettingsListDensity, AppStrings.SettingsListDensityDescription), density),
                    Section(AppStrings.UpdateHeading,
                        new Label { Text = Localized.Format(AppStrings.UpdateInstalledVersion, AppInfo.Current.VersionString), FontSize = 15, FontAttributes = FontAttributes.Bold },
                        updateStatus, updateProgress, checkUpdate, installUpdate, cancelUpdate),
                    Section(AppStrings.SettingsSectionAbout, repository)
                }
            }
        };
        ReaderTheme.Apply(this);
    }

    private static VerticalStackLayout RowTitle(string title, string description) => new()
    {
        Spacing = 2,
        Children =
        {
            new Label { Text = title, FontSize = 15, FontAttributes = FontAttributes.Bold },
            ReaderTheme.Metadata(new Label { Text = description, FontSize = 12 })
        }
    };

    private static VerticalStackLayout Section(string title, params View[] content)
    {
        var heading = new Label { Text = title, FontSize = 13, FontAttributes = FontAttributes.Bold, Margin = new Thickness(8, 14, 8, 4) };
        heading.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level2);
        var body = new VerticalStackLayout { Spacing = 10 };
        foreach (var view in content) body.Add(view);
        var card = ReaderTheme.Panel(body, 16);
        card.StrokeThickness = 0;
        card.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 };
        return new VerticalStackLayout { Children = { heading, card } };
    }

    private static async Task OpenRepositoryAsync()
    {
        var uri = new Uri("https://github.com/iricigor/OBK");
        try
        {
            if (await Launcher.TryOpenAsync(uri)) return;
        }
        catch (global::Android.Content.ActivityNotFoundException)
        {
            // A handler may disappear between discovery and launch; retry without discovery.
        }
        if (!await Browser.OpenAsync(uri, BrowserLaunchMode.External))
            throw new ObkException(AppStrings.ErrorNoAppForLink);
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

}
