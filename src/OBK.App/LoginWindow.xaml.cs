using OBK.Core;
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

    public LoginWindow(MainWindow app, Login? initial, bool remembered, string error, bool showSavedAccounts = false)
    {
        this.app = app;
        this.initial = initial;
        this.showSavedAccounts = showSavedAccounts;
        Owner = app;
        InitializeComponent();
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
            if (!schoolConfirmed) throw new ObkException("Choose a school before signing in.");
            var url = KomensClient.NormalizeUrl(SchoolUrl.Text);
            var username = Username.Text.Trim();
            var password = Password.Password;
            if (username.Length == 0) throw new ObkException("Enter your Komens username.");
            if (password.Length == 0) throw new ObkException("Enter your Komens password.");
            var remember = Remember.IsChecked == true;
            var childName = OptionalLabel(ChildName.Text);
            SetBusy(true);
            Status.Text = "Connecting to the school...";
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
            if (!schoolConfirmed) throw new ObkException("Choose a school before opening offline messages.");
            var username = Username.Text.Trim();
            if (username.Length == 0) throw new ObkException("Enter the username used to download the offline messages.");
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
            Status.Text = "Wait for the current operation to finish.";
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
                    throw new ObkException("The school directory is unavailable. Enter the school website manually.");
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
            SchoolSearchStatus.Text = "Search by school name, town, or website.";
            return;
        }
        var result = schoolDirectory.Search(query);
        SchoolMatches.ItemsSource = result.Visible;
        SchoolMatches.SelectedIndex = -1;
        if (result.Total == 0)
        {
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = "No schools found. Check the spelling or enter the school website.";
        }
        else if (result.Total > result.Visible.Count)
        {
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = $"{result.Total:N0} matches. Type a little more to narrow the list.";
        }
        else
        {
            SchoolMatches.Visibility = Visibility.Visible;
            SchoolSearchStatus.Text = $"{result.Total} {(result.Total == 1 ? "match" : "matches")}. Use up and down, then Enter to choose.";
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
        ShowConfirmedSchool(row.SchoolLabel, row.DirectorySchool?.Towns ?? "Saved account",
            KomensClient.NormalizeUrl(profile.Url), moveFocus: false);
        Username.Text = profile.Username;
        Password.Password = profile.Password ?? "";
        ChildName.Text = profile.ChildName ?? "";
        SetRemember(profile.Password is not null);
        Status.Text = profile.Password is null ?
            "Previous account selected. Enter the password." :
            "Saved account ready. Sign in and sync, or read offline.";
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
        ShowConfirmedSchool(known?.Name ?? knownSchoolName, known?.Towns ?? "Entered manually", normalized, moveFocus);
    }

    private void ShowConfirmedSchool(string? name, string towns, string normalizedUrl, bool moveFocus)
    {
        selectedSchoolName = name;
        schoolConfirmed = true;
        updatingUrl = true;
        SchoolUrl.Text = normalizedUrl;
        updatingUrl = false;
        SelectedSchoolName.Text = name ?? "School website";
        SelectedSchoolTowns.Text = towns;
        SelectedSchoolUrl.Text = normalizedUrl;
        SchoolChooserPanel.Visibility = Visibility.Collapsed;
        ManualUrlPanel.Visibility = Visibility.Collapsed;
        SelectedSchoolPanel.Visibility = Visibility.Visible;
        CredentialsPanel.Visibility = Visibility.Visible;
        Status.Text = "School selected. Enter your Komens username and password.";
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
    public string Title => Login.ChildName is { Length: > 0 } child ? child : SchoolLabel ?? "Saved account";
    public string Detail => string.Join(" \u2022 ", new[]
    {
        Login.ChildName is { Length: > 0 } ? SchoolLabel : null,
        Login.Username,
        Uri.TryCreate(Login.Url, UriKind.Absolute, out var uri) ? uri.Host : Login.Url
    }.Where(part => !string.IsNullOrEmpty(part)));
}
