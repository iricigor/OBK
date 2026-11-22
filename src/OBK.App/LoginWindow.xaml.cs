using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OBK.App;

public partial class LoginWindow : Window
{
    private readonly MainWindow app;
    private readonly Login? initial;
    private readonly bool showSavedAccounts;
    private bool completed;
    private bool ready;
    private bool suppressRememberChange;
    private bool busy;
    private bool updatingSearch;
    private bool updatingUrl;
    private bool schoolConfirmed;
    private SchoolDirectory? schoolDirectory;
    private IReadOnlyList<SavedLoginRow> rememberedProfiles = [];
    private string? selectedSchoolName;
    public string StatusText { set => Status.Text = value; }
    internal Task DirectoryReady { get; }
    internal int AvailableSchoolCount => schoolDirectory?.Count ?? 0;
    internal FormState? LanguageForm { get; private set; }
    internal sealed record FormState(string Url, string Username, string Password, string ChildName,
        bool Remember, string Search, bool Confirmed, string? SchoolName, bool Manual);

    public LoginWindow(MainWindow app, Login? initial, bool remembered, string error, bool showSavedAccounts = false)
    {
        this.app = app;
        this.initial = initial;
        this.showSavedAccounts = showSavedAccounts;
        Owner = app;
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(LanguageSettings.Culture.Name);
        LanguageButton.Content = app.LanguageCode;
        LanguageButton.ToolTip = app.LanguageTooltip;
        System.Windows.Automation.AutomationProperties.SetName(LanguageButton, app.LanguageTooltip);
        UpdateThemeButton();
        MaxHeight = SystemParameters.WorkArea.Height;
        SizeChanged += (_, _) => KeepOnScreen();
        SchoolUrl.Text = initial?.Url ?? "";
        Username.Text = initial?.Username ?? "";
        ChildName.Text = initial?.ChildName ?? "";
        Password.Password = initial?.Password ?? "";
        Remember.IsChecked = remembered;
        Status.Text = error;
        ready = true;
        DirectoryReady = LoadSchoolDirectoryAsync();
        Loaded += async (_, _) =>
        {
            await DirectoryReady;
            if (!schoolConfirmed)
            {
                if (ManualUrlPanel.Visibility == Visibility.Visible) SchoolUrl.Focus();
                else if (SchoolSearch.IsEnabled) SchoolSearch.Focus();
            }

            else if (Username.Text.Length > 0) Password.Focus();
            else Username.Focus();
        };
    }

    private void SwitchLanguage(object sender, RoutedEventArgs e)
    {
        if (busy || app.Busy) { Status.Text = SharedStrings.ErrorLanguageBusy; return; }
        LanguageForm = new(SchoolUrl.Text, Username.Text, Password.Password, ChildName.Text,
            Remember.IsChecked == true, SchoolSearch.Text, schoolConfirmed, selectedSchoolName,
            ManualUrlPanel.Visibility == Visibility.Visible);
        completed = true;
        Close();
    }

    internal async Task RestoreFormAsync(FormState form)
    {
        await DirectoryReady;
        if (form.Confirmed) ConfirmManualSchool(form.Url, form.SchoolName, moveFocus: false);
        else
        {
            schoolConfirmed = false;
            SchoolChooserPanel.Visibility = Visibility.Visible;
            SelectedSchoolPanel.Visibility = CredentialsPanel.Visibility = Visibility.Collapsed;
            SchoolSearch.Text = form.Search;
            if (form.Manual) ShowManualUrl();
            SchoolUrl.Text = form.Url;
        }
        Username.Text = form.Username;
        Password.Password = form.Password;
        ChildName.Text = form.ChildName;
        SetRemember(form.Remember);
        UpdateActionButtons();
    }

    private void UpdateThemeButton()
    {
        ThemeIcon.Text = app.ViewModel.Theme.ToggleIcon;
        if (ThemeIcon.Parent is System.Windows.Controls.Button button)
        {
            button.ToolTip = app.ViewModel.Theme.ToggleTooltip;
            System.Windows.Automation.AutomationProperties.SetName(button, app.ViewModel.Theme.ToggleTooltip);
        }
    }

    private async void SwitchTheme(object sender, RoutedEventArgs e)
    {
        try { await app.ViewModel.Theme.ToggleAsync(); UpdateThemeButton(); }
        catch (ObkException error) { Status.Text = error.Message; }
    }

    private async void RememberUnchecked(object sender, RoutedEventArgs e)
    {
        if (!ready || suppressRememberChange || !schoolConfirmed) return;
        var username = Username.Text.Trim();
        if (username.Length == 0) return;
        SetBusy(true);
        try
        {
            var url = KomensClient.NormalizeUrl(SchoolUrl.Text);
            await Task.Run(() => app.Store.ForgetLogin(url, username));
            rememberedProfiles = rememberedProfiles
                .Select(profile => string.Equals(profile.Login.Url, url, StringComparison.Ordinal) &&
                    string.Equals(profile.Login.Username, username, StringComparison.Ordinal)
                    ? new SavedLoginRow(profile.Login with { Password = null }, profile.DirectorySchool)
                    : profile)
                .ToArray();
            ShowRememberedProfiles();
        }
        catch (ObkException error) { Status.Text = error.Message; SetRemember(true); }
        finally { SetBusy(false); }
    }

    private void SetRemember(bool value)
    {
        suppressRememberChange = true;
        Remember.IsChecked = value;
        suppressRememberChange = false;
    }

    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ready && !busy && Password.Password.Length > 0) Status.Text = "";
    }

    public void SetBusy(bool busy)
    {
        this.busy = busy;
        LanguageButton.IsEnabled = !busy;
        SchoolSearch.IsEnabled = schoolDirectory is not null && !busy;
        ManualUrlButton.IsEnabled = schoolDirectory is not null && !busy;
        BackToSchoolSearchButton.IsEnabled = schoolDirectory is not null && !busy;
        SchoolMatches.IsEnabled = SchoolUrl.IsEnabled = Username.IsEnabled = ChildName.IsEnabled =
            Password.IsEnabled = Remember.IsEnabled = UseManualUrlButton.IsEnabled =
            ChangeSchoolButton.IsEnabled = !busy;
        SignInButton.IsEnabled = OfflineButton.IsEnabled = schoolConfirmed && !busy;
    }

    private async void SignIn(object sender, RoutedEventArgs e)
    {
        if (app.Busy || busy) return;
        try
        {
            if (!schoolConfirmed) throw new ObkException(AppStrings.LoginErrorChooseSchool);
            var url = KomensClient.NormalizeUrl(SchoolUrl.Text);
            var username = Username.Text.Trim();
            var password = Password.Password;
            if (username.Length == 0) throw new ObkException(AppStrings.LoginErrorUsername);
            if (password.Length == 0) throw new ObkException(AppStrings.LoginErrorPassword);
            var remember = Remember.IsChecked == true;
            var childName = OptionalLabel(ChildName.Text);
            SetBusy(true);
            Status.Text = AppStrings.LoginConnecting;
            await app.SynchronizeAsync(this, url, username, password, remember, false, childName,
                selectedSchoolName);
        }
        catch (ObkException error) { Status.Text = error.Message; SetBusy(false); }
    }

    public void Complete()
    {
        completed = true;
        Password.Clear();
        Close();
    }

    private async Task<bool> ActivateOfflineAsync()
    {
        if (app.Busy) return false;
        try
        {
            if (!schoolConfirmed) throw new ObkException(AppStrings.LoginErrorChooseSchoolOffline);
            var username = Username.Text.Trim();
            if (username.Length == 0) throw new ObkException(AppStrings.LoginErrorOfflineUsername);
            var url = KomensClient.NormalizeUrl(SchoolUrl.Text);
            var childName = OptionalLabel(ChildName.Text);
            await Task.Run(() => app.Store.SaveAccount(url, username, childName, selectedSchoolName));
            await app.ActivateAccountAsync(url, username, childName, selectedSchoolName);
            app.OfflineStatus();
            return true;
        }
        catch (ObkException error) { Status.Text = error.Message; return false; }
    }

    private async void ReadOffline(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try { if (await ActivateOfflineAsync()) Complete(); }
        finally { SetBusy(false); }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (completed) { Password.Clear(); return; }
        if (busy || app.Busy)
        {
            e.Cancel = true;
            Status.Text = AppStrings.StatusWaitForOperation;
            return;
        }
        Password.Clear();
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private async Task LoadSchoolDirectoryAsync()
    {
        try
        {
            var loaded = await Task.Run(() =>
            {
                const string resource = "OBK.App.Data.schools.json.gz";
                using var stream = typeof(LoginWindow).Assembly.GetManifestResourceStream(resource) ??
                    throw new ObkException(AppStrings.LoginErrorDirectoryUnavailable);
                return SchoolDirectory.Load(stream);
            });
            schoolDirectory = loaded;
            try
            {
                rememberedProfiles = await Task.Run(() => app.Store.KnownLogins()
                    .Select(profile => new SavedLoginRow(profile, loaded.FindByUrl(profile.Url))).ToArray());
            }
            catch (ObkException error) { Status.Text = error.Message; }
            ShowRememberedProfiles();
            var match = initial?.Url is { Length: > 0 } url ? loaded.FindByUrl(url) : null;
            if (showSavedAccounts)
                UpdateSchoolSearch();
            else if (match is not null)
                ConfirmSchool(match, moveFocus: false);
            else if (initial?.Url is { Length: > 0 })
                ConfirmManualSchool(initial.Url, initial.SchoolName, moveFocus: false);
            else
                UpdateSchoolSearch();
            SchoolSearch.IsEnabled = !busy;
        }
        catch (ObkException error)
        {
            Status.Text = error.Message;
            ShowManualUrl();
            ManualUrlButton.IsEnabled = false;
            BackToSchoolSearchButton.IsEnabled = false;
        }
    }

    private void SchoolSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (updatingSearch || schoolDirectory is null) return;
        schoolConfirmed = false;
        updatingUrl = true;
        SchoolUrl.Text = "";
        updatingUrl = false;
        UpdateSchoolSearch();
        UpdateActionButtons();
    }

    private void SchoolUrlChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!updatingUrl)
        {
            schoolConfirmed = false;
            selectedSchoolName = null;
        }
        UpdateActionButtons();
    }

    private void SchoolSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (SchoolMatches.Visibility != Visibility.Visible || SchoolMatches.Items.Count == 0) return;
        if (e.Key == Key.Down)
        {
            if (SchoolMatches.SelectedIndex < 0) SchoolMatches.SelectedIndex = 0;
            SchoolMatches.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (SchoolMatches.SelectedItem is SchoolDirectoryEntry)
                CommitSchoolSelection();
            else if (SchoolMatches.Items.Count == 1)
            {
                SchoolMatches.SelectedIndex = 0;
                CommitSchoolSelection();
            }
            else
            {
                SchoolMatches.SelectedIndex = 0;
                SchoolMatches.Focus();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SchoolSearch.Text = "";
            e.Handled = true;
        }
    }

    private void SchoolMatchesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSchoolSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SchoolSearch.Focus();
            e.Handled = true;
        }
    }

    private void SchoolMatchesMouseUp(object sender, MouseButtonEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, CommitSchoolSelection);
    }

    internal void CommitSchoolSelection()
    {
        if (SchoolMatches.SelectedItem is SchoolDirectoryEntry school)
            ConfirmSchool(school);
    }

    private void UpdateSchoolSearch()
    {
        if (schoolDirectory is null) return;
        ShowRememberedProfiles();
        var query = SchoolSearch.Text.Trim();
        if (query.Length == 0)
        {
            SchoolMatches.ItemsSource = null;
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = AppStrings.LoginSearchPrompt;
            return;
        }
        var result = schoolDirectory.Search(query);
        SchoolMatches.ItemsSource = result.Visible;
        SchoolMatches.SelectedIndex = -1;
        if (result.Total == 0)
        {
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = AppStrings.LoginNoSchools;
        }
        else if (result.Total > result.Visible.Count)
        {
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = Localized.Format(AppStrings.LoginTooManyMatches, result.Total);
        }
        else
        {
            SchoolMatches.Visibility = Visibility.Visible;
            SchoolSearchStatus.Text = Localized.Format(result.Total == 1 ? AppStrings.LoginOneMatch : AppStrings.LoginMatches, result.Total);
        }
    }

    private void ConfirmSchool(SchoolDirectoryEntry school, bool moveFocus = true)
    {
        var normalized = KomensClient.NormalizeUrl(school.SchoolUrl);
        ShowConfirmedSchool(school.Name, school.Towns, normalized, moveFocus);
    }

    private void OpenManualUrl(object sender, RoutedEventArgs e)
    {
        ClearAccountFields();
        ShowManualUrl();
        SchoolUrl.Focus();
    }

    private void SavedAccountSelected(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: SavedLoginRow row })
            ConfirmRememberedProfile(row, moveFocus: true);
    }

    internal void UseRememberedProfile(int index)
    {
        if (index < 0 || index >= rememberedProfiles.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        ConfirmRememberedProfile(rememberedProfiles[index], moveFocus: false);
    }

    internal int RememberedProfileCount => rememberedProfiles.Count;

    private void ConfirmRememberedProfile(SavedLoginRow row, bool moveFocus)
    {
        var profile = row.Login;
        ShowConfirmedSchool(row.SchoolLabel, row.DirectorySchool?.Towns ?? AppStrings.LoginSavedAccount,
            KomensClient.NormalizeUrl(profile.Url), moveFocus: false);
        Username.Text = profile.Username;
        Password.Password = profile.Password ?? "";
        ChildName.Text = profile.ChildName ?? "";
        SetRemember(profile.Password is not null);
        Status.Text = profile.Password is null ?
            AppStrings.LoginPreviousAccountSelected :
            AppStrings.LoginSavedAccountReady;
        if (moveFocus) Password.Focus();
    }

    private void BackToSchoolSearch(object sender, RoutedEventArgs e)
    {
        ClearAccountFields();
        schoolConfirmed = false;
        SchoolChooserPanel.Visibility = Visibility.Visible;
        ManualUrlPanel.Visibility = Visibility.Collapsed;
        SelectedSchoolPanel.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Collapsed;
        Status.Text = "";
        UpdateSchoolSearch();
        UpdateActionButtons();
        SchoolSearch.Focus();
    }

    private void SchoolUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            UseManualUrl(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void UseManualUrl(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfirmManualSchool(SchoolUrl.Text, selectedSchoolName);
        }
        catch (ObkException error)
        {
            Status.Text = error.Message;
            SchoolUrl.Focus();
        }
    }

    private void ConfirmManualSchool(string url, string? knownSchoolName, bool moveFocus = true)
    {
        var normalized = KomensClient.NormalizeUrl(url);
        var known = schoolDirectory?.FindByUrl(normalized);
        ShowConfirmedSchool(known?.Name ?? knownSchoolName, known?.Towns ?? AppStrings.LoginEnteredManually, normalized, moveFocus);
    }

    private void ShowConfirmedSchool(string? name, string towns, string normalizedUrl, bool moveFocus)
    {
        selectedSchoolName = name;
        schoolConfirmed = true;
        updatingUrl = true;
        SchoolUrl.Text = normalizedUrl;
        updatingUrl = false;
        SelectedSchoolName.Text = name ?? AppStrings.LoginSchoolWebsite;
        SelectedSchoolTowns.Text = towns;
        SelectedSchoolUrl.Text = normalizedUrl;
        SchoolChooserPanel.Visibility = Visibility.Collapsed;
        ManualUrlPanel.Visibility = Visibility.Collapsed;
        SelectedSchoolPanel.Visibility = Visibility.Visible;
        CredentialsPanel.Visibility = Visibility.Visible;
        Status.Text = AppStrings.LoginSchoolSelected;
        UpdateActionButtons();
        if (moveFocus) Username.Focus();
    }

    private void ChangeSchool(object sender, RoutedEventArgs e)
    {
        ClearAccountFields();
        schoolConfirmed = false;
        SchoolChooserPanel.Visibility = Visibility.Visible;
        ManualUrlPanel.Visibility = Visibility.Collapsed;
        SelectedSchoolPanel.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Collapsed;
        Status.Text = "";
        updatingSearch = true;
        SchoolSearch.Text = selectedSchoolName ?? "";
        updatingSearch = false;
        updatingUrl = true;
        SchoolUrl.Text = "";
        updatingUrl = false;
        UpdateSchoolSearch();
        UpdateActionButtons();
        SchoolSearch.Focus();
        SchoolSearch.SelectAll();
    }

    private void ShowManualUrl()
    {
        schoolConfirmed = false;
        SchoolChooserPanel.Visibility = Visibility.Collapsed;
        ManualUrlPanel.Visibility = Visibility.Visible;
        SelectedSchoolPanel.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Collapsed;
        Status.Text = "";
        UpdateActionButtons();
    }

    private void ShowRememberedProfiles()
    {
        SavedAccounts.ItemsSource = rememberedProfiles;
        var visible = rememberedProfiles.Count > 0 && SchoolSearch.Text.Trim().Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        SavedAccountsPanel.Visibility = SavedAccountsDivider.Visibility = visible;
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        if (Left < area.Left || Left >= area.Right || Top < area.Top || Top >= area.Bottom) return;
        if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }

    private void UpdateActionButtons()
    {
        if (SignInButton is null) return;
        SignInButton.IsEnabled = OfflineButton.IsEnabled = schoolConfirmed && !busy;
    }

    private void ClearAccountFields()
    {
        Username.Text = "";
        Password.Clear();
        ChildName.Text = "";
        SetRemember(false);
    }

    private static string? OptionalLabel(string value) => value.Trim() is { Length: > 0 } label ? label : null;
}

public sealed class SavedLoginRow(Login login, SchoolDirectoryEntry? directorySchool)
{
    public Login Login { get; } = login;
    public SchoolDirectoryEntry? DirectorySchool { get; } = directorySchool;
    public string? SchoolLabel => DirectorySchool?.Name ?? Login.SchoolName;
    public string Title => Login.ChildName is { Length: > 0 } child ? child : SchoolLabel ?? AppStrings.LoginSavedAccount;
    public string Detail => string.Join(" \u2022 ", new[]
    {
        Login.ChildName is { Length: > 0 } ? SchoolLabel : null,
        Login.Username,
        Uri.TryCreate(Login.Url, UriKind.Absolute, out var uri) ? uri.Host : Login.Url
    }.Where(part => !string.IsNullOrEmpty(part)));
}
