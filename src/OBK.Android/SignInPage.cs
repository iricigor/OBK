using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class SignInPage : ContentPage
{
    private readonly ReaderSession session;
    private readonly Func<Login, Task> saved;
    private readonly Entry url = new() { Placeholder = "https://your-school.bakalari.cz", Keyboard = Keyboard.Url };
    private readonly Entry username = new() { Placeholder = AppStrings.SignInUsernamePlaceholder, IsTextPredictionEnabled = false };
    private readonly Entry password = new() { Placeholder = AppStrings.SignInPasswordPlaceholder, IsPassword = true, IsTextPredictionEnabled = false };
    private readonly Entry child = new() { Placeholder = AppStrings.SignInChildPlaceholder };
    private readonly CheckBox remember = new() { MinimumHeightRequest = 48, MinimumWidthRequest = 48 };
    private readonly SearchBar schoolSearch = new() { Placeholder = AppStrings.SignInSchoolSearchPlaceholder };
    private readonly VerticalStackLayout schoolResults = new() { Spacing = 8 };
    private readonly Label schoolStatus = ReaderTheme.Metadata(new Label { IsVisible = false });
    private readonly Label status = ReaderTheme.Metadata(new Label { Text = AppStrings.SignInPasswordNotice });
    private readonly Button submit = ReaderTheme.Primary(new Button { Text = AppStrings.SignInSubmitButton });
    private readonly Button manualAddress = ReaderTheme.Subtle(new Button { Text = AppStrings.SignInManualAddressButton });
    private readonly ActivityIndicator progress = new()
    {
        IsVisible = false, WidthRequest = 24, HeightRequest = 24, Margin = new Thickness(0, 0, 8, 0), VerticalOptions = LayoutOptions.Center
    };
    private readonly Label success = new() { Text = SharedStrings.DownloadLoginSuccessful, FontSize = 18, FontAttributes = FontAttributes.Bold, IsVisible = false };
    private readonly Label messagesReady = new() { Text = SharedStrings.DownloadMessagesReady, IsVisible = false };
    private readonly Label attachmentCount = new() { FontAttributes = FontAttributes.Bold, IsVisible = false };
    private readonly ProgressBar downloadProgress = new() { HeightRequest = 4, IsVisible = false };
    private readonly Button background = ReaderTheme.Primary(new Button { Text = SharedStrings.DownloadInBackground, LineBreakMode = LineBreakMode.WordWrap, IsVisible = false });
    private readonly Label backgroundHint = ReaderTheme.Metadata(new Label { Text = SharedStrings.DownloadBackgroundHint, IsVisible = false });
    private readonly Label keepOpen = ReaderTheme.Metadata(new Label { Text = SharedStrings.DownloadKeepOpen, IsVisible = false });
    private readonly Button scan = new() { Text = AppStrings.SignInScanFromPc };
    private readonly VerticalStackLayout downloadPanel;
    private readonly VerticalStackLayout addressFields;
    private bool busy;
    private InitialDownload? download;
    private Task? enterReader;
    private bool handoffRequested;
    private bool handedOff;
    private readonly Login? initial;
    internal bool Busy => busy;

    public SignInPage(ReaderSession session, Login? login, Func<Login, Task> saved)
    {
        this.session = session;
        this.saved = saved;
        initial = login;
        LanguageToolbar.Add(this);
        Title = login is null ? AppStrings.SignInTitleAddChild : AppStrings.SignInTitleSignIn;
        addressFields = Field(AppStrings.SignInSchoolAddressLabel, url);
        addressFields.IsVisible = login is not null;
        manualAddress.Text = addressFields.IsVisible ? AppStrings.SignInHideAddressButton : AppStrings.SignInManualAddressButton;
        manualAddress.Clicked += (_, _) =>
        {
            addressFields.IsVisible = !addressFields.IsVisible;
            manualAddress.Text = addressFields.IsVisible ? AppStrings.SignInHideAddressButton : AppStrings.SignInManualAddressButton;
            if (addressFields.IsVisible) url.Focus();
        };
        if (login is not null)
        {
            url.Text = login.Url;
            username.Text = login.Username;
            child.Text = login.ChildName;
            password.Text = login.Password;
            remember.IsChecked = login.Password is not null;
        }

        schoolSearch.TextChanged += (_, _) =>
        {
            schoolResults.Children.Clear();
            var found = session.Schools.Search(schoolSearch.Text ?? "");
            schoolStatus.Text = string.IsNullOrWhiteSpace(schoolSearch.Text) ? "" :
                found.Total == 0 ? AppStrings.SignInNoSchoolMatch :
                found.Total > 6 ? Localized.Format(AppStrings.SignInTooManySchools, found.Total) :
                Localized.Format(AppStrings.SignInMatchingSchools, found.Total);
            schoolStatus.IsVisible = schoolStatus.Text.Length > 0;
            if (found.Total > 6) return;
            foreach (var school in found.Visible)
            {
                var choose = new Button { Text = $"{school.Name}\n{school.Towns}", LineBreakMode = LineBreakMode.WordWrap };
                choose.Clicked += (_, _) =>
                {
                    url.Text = school.SchoolUrl;
                    schoolResults.Children.Clear();
                    schoolStatus.Text = school.Name;
                    schoolStatus.IsVisible = true;
                };
                schoolResults.Add(choose);
            }
            ReaderTheme.Apply(schoolResults);
        };
        submit.Clicked += async (_, _) => await SignInAsync();
        scan.Clicked += async (_, _) => { if (!busy) await TransferScanPage.OpenAsync(this, Import); };
        background.Clicked += async (_, _) =>
        {
            if (download?.State.MessagesReady != true) return;
            background.IsEnabled = false;
            try { await EnterReaderAsync(); }
            catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
            {
                enterReader = null;
                handoffRequested = false;
                status.Text = error.Message;
                await DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
            }
            finally { background.IsEnabled = true; }
        };
        var remembered = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = 8
        };
        remembered.Add(remember, 0);
        remembered.Add(new Label { Text = AppStrings.SignInRememberCredentials, FontSize = 14, VerticalOptions = LayoutOptions.Center }, 1);
        var form = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 12,
                Children =
                {
                    new Label { Text = AppStrings.SignInHeading, FontSize = 20, FontAttributes = FontAttributes.Bold },
                    scan, ReaderTheme.Metadata(new Label { Text = AppStrings.SignInScanHint }),
                    schoolSearch, schoolStatus, schoolResults,
                    manualAddress, addressFields,
                    Field(AppStrings.SignInUsernameLabel, username),
                    Field(AppStrings.SignInPasswordLabel, password),
                    Field(AppStrings.SignInChildLabel, child),
                    remembered, submit, ReaderTheme.Metadata(new Label { Text = AppStrings.SignInPasswordNotice }),
                    ReaderTheme.Metadata(new Label { Text = AppStrings.SignInReadOnlyNotice })
                }
            }
        };
        success.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.StatusGood, ReaderTheme.DarkStatusGood);
        progress.SetAppThemeColor(ActivityIndicator.ColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        downloadProgress.SetAppThemeColor(ProgressBar.ProgressColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        SemanticProperties.SetHeadingLevel(success, SemanticHeadingLevel.Level2);
        var statusRow = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
        statusRow.Add(progress);
        statusRow.Add(status, 1);
        downloadPanel = new VerticalStackLayout
        {
            Padding = 16, Spacing = 8, IsVisible = false,
            Children = { success, messagesReady, attachmentCount, downloadProgress, statusRow, background, backgroundHint, keepOpen }
        };
        downloadPanel.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Surface, ReaderTheme.DarkSurface);
        var layout = new Grid { RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(form);
        layout.Add(downloadPanel, 0, 1);
        Content = layout;
        ReaderTheme.Apply(this);
    }

    internal SignInPage Rebuild(Func<Login, Task> saved)
    {
        var rebuilt = new SignInPage(session, initial, saved);
        rebuilt.url.Text = url.Text;
        rebuilt.username.Text = username.Text;
        rebuilt.password.Text = password.Text;
        rebuilt.child.Text = child.Text;
        rebuilt.remember.IsChecked = remember.IsChecked;
        rebuilt.schoolSearch.Text = schoolSearch.Text;
        rebuilt.addressFields.IsVisible = addressFields.IsVisible;
        rebuilt.manualAddress.Text = addressFields.IsVisible ? AppStrings.SignInHideAddressButton : AppStrings.SignInManualAddressButton;
        return rebuilt;
    }
    private static VerticalStackLayout Field(string label, View input) => new()
    {
        Spacing = 4,
        Children = { ReaderTheme.Metadata(new Label { Text = label }), ReaderTheme.Panel(input, new Thickness(8, 0)) }
    };

    // Fills the form from a PC transfer; the user still reviews it and decides whether to remember the password.
    private void Import(Login login)
    {
        url.Text = login.Url;
        addressFields.IsVisible = true;
        manualAddress.Text = AppStrings.SignInHideAddressButton;
        username.Text = login.Username;
        password.Text = login.Password;
        child.Text = login.ChildName;
        remember.IsChecked = false;
        schoolResults.Children.Clear();
        schoolStatus.Text = login.SchoolName ?? "";
        schoolStatus.IsVisible = schoolStatus.Text.Length > 0;
        ShowStatus(AppStrings.SignInImported);
    }

    // The status shares the anchored download panel, so other messages hide any earlier download state.
    private void ShowStatus(string text)
    {
        success.IsVisible = messagesReady.IsVisible = attachmentCount.IsVisible = background.IsVisible =
            backgroundHint.IsVisible = keepOpen.IsVisible = downloadProgress.IsVisible = false;
        status.Text = text;
        downloadPanel.IsVisible = true;
    }

    private async Task SignInAsync()
    {
        if (busy) return;
        busy = true;
        handoffRequested = handedOff = false;
        enterReader = null;
        download = null;
        ShowStatus(AppStrings.StatusSigningIn);
        submit.IsEnabled = false;
        scan.IsEnabled = false;
        NavigationPage.SetHasBackButton(this, false);
        progress.IsRunning = progress.IsVisible = ReaderTheme.AnimationsEnabled;
        manualAddress.IsEnabled = false;
        schoolSearch.IsEnabled = url.IsEnabled = username.IsEnabled = password.IsEnabled = child.IsEnabled = remember.IsEnabled = false;
        schoolResults.IsEnabled = false;
        try
        {
            var user = username.Text?.Trim() ?? "";
            var secret = password.Text ?? "";
            if (user.Length == 0 || secret.Length == 0)
                throw new ObkException(AppStrings.ErrorEnterUsernameAndPassword);
            var address = KomensClient.NormalizeUrl(url.Text ?? "");
            var login = new Login(address, user, ChildName: string.IsNullOrWhiteSpace(child.Text) ? null : child.Text.Trim());
            download = new(login, SchoolYear.Current());
            session.DownloadChanged += DownloadChanged;
            await session.SynchronizeInitialAsync(download, secret, remember.IsChecked, text => status.Text = text);
            await EnterReaderAsync();
        }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            if (download is not null) ApplyDownloadState(download.State.Error is null ? download.State with { Error = error.Message } : download.State);
            else status.Text = error.Message;
            if (!handoffRequested)
                await DisplayAlertAsync(download?.State.Authenticated == true ? SharedStrings.DownloadDetails : AppStrings.SignInFailedAlertTitle,
                    status.Text, AppStrings.AlertOkButton);
            else if (enterReader is not null) await enterReader;
        }
        finally
        {
            busy = false;
            session.DownloadChanged -= DownloadChanged;
            submit.IsEnabled = true;
            scan.IsEnabled = true;
            NavigationPage.SetHasBackButton(this, true);
            progress.IsRunning = false;
            progress.IsVisible = false;
            manualAddress.IsEnabled = true;
            schoolSearch.IsEnabled = url.IsEnabled = username.IsEnabled = password.IsEnabled = child.IsEnabled = remember.IsEnabled = true;
            schoolResults.IsEnabled = true;
        }
    }

    private void DownloadChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (!handedOff && download is not null && ReferenceEquals(session.Download, download))
            ApplyDownloadState(download.State);
    });

    private void ApplyDownloadState(InitialDownloadState state)
    {
        success.IsVisible = state.Authenticated;
        messagesReady.IsVisible = attachmentCount.IsVisible = downloadProgress.IsVisible = state.MessagesReady;
        attachmentCount.Text = state.AttachmentCount;
        downloadProgress.Progress = state.Fraction;
        progress.IsRunning = progress.IsVisible = state.Running && ReaderTheme.AnimationsEnabled;
        status.Text = state.Status;
        background.IsVisible = state.CanContinueInBackground || (state.MessagesReady && state.Error is not null);
        background.Text = state.Error is null ? SharedStrings.DownloadInBackground : SharedStrings.DownloadReadMessages;
        backgroundHint.IsVisible = keepOpen.IsVisible = state.CanContinueInBackground;
    }

    private Task EnterReaderAsync() => enterReader ??= EnterReaderCoreAsync();

    private async Task EnterReaderCoreAsync()
    {
        if (download is null) throw new ObkException(SharedStrings.DownloadWait);
        session.ReadWhileDownloading();
        handoffRequested = true;
        await saved(download.Account);
        password.Text = "";
        if (Navigation.NavigationStack.LastOrDefault() == this) await Navigation.PopAsync();
        handedOff = true;
    }

    protected override bool OnBackButtonPressed()
    {
        if (!busy) return base.OnBackButtonPressed();
        SemanticScreenReader.Default.Announce(SharedStrings.DownloadWait);
        return true;
    }
}
