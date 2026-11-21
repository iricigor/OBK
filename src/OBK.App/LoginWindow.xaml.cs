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
    private bool completed;
    private bool ready;
    private bool busy;
    private bool updatingSearch;
    private bool updatingUrl;
    private bool schoolConfirmed;
    private SchoolDirectory? schoolDirectory;
    private string? selectedSchoolName;
    public string StatusText { set => Status.Text = value; }
    internal Task DirectoryReady { get; }
    internal int AvailableSchoolCount => schoolDirectory?.Count ?? 0;

    public LoginWindow(MainWindow app, Login? initial, bool remembered, string error)
    {
        this.app = app;
        this.initial = initial;
        Owner = app;
        InitializeComponent();
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
        if (!ready) return;
        SetBusy(true);
        try { await Task.Run(app.Store.ForgetCredentials); }
        catch (ObkException error) { Status.Text = error.Message; Remember.IsChecked = true; }
        finally { SetBusy(false); }
    }

    public void SetBusy(bool busy)
    {
        this.busy = busy;
        SchoolSearch.IsEnabled = schoolDirectory is not null && !busy;
        ManualUrlButton.IsEnabled = schoolDirectory is not null && !busy;
        BackToSchoolSearchButton.IsEnabled = schoolDirectory is not null && !busy;
        SchoolMatches.IsEnabled = SchoolUrl.IsEnabled = Username.IsEnabled = ChildName.IsEnabled =
            Password.IsEnabled = Remember.IsEnabled = FullRefresh.IsEnabled = UseManualUrlButton.IsEnabled =
            ChangeSchoolButton.IsEnabled = AdvancedOptions.IsEnabled = !busy;
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
            var fullRefresh = FullRefresh.IsChecked == true;
            var childName = OptionalLabel(ChildName.Text);
            SetBusy(true);
            if (!remember) await Task.Run(app.Store.ForgetCredentials);
            Status.Text = $"Connecting to {SelectedSchoolName.Text}...";
            await app.SynchronizeAsync(this, url, username, password, remember, fullRefresh, childName, selectedSchoolName);
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
            var match = initial?.Url is { Length: > 0 } url ? loaded.FindByUrl(url) : null;
            if (match is not null)
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
        if (!updatingUrl) schoolConfirmed = false;
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
        var query = SchoolSearch.Text.Trim();
        if (query.Length == 0)
        {
            SchoolMatches.ItemsSource = null;
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = $"Search {schoolDirectory.Count:N0} bundled schools by name, town, or website.";
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
        ShowManualUrl();
        SchoolUrl.Focus();
    }

    private void BackToSchoolSearch(object sender, RoutedEventArgs e)
    {
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
            ConfirmManualSchool(SchoolUrl.Text, initial?.SchoolName);
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

    private void UpdateActionButtons()
    {
        if (SignInButton is null) return;
        SignInButton.IsEnabled = OfflineButton.IsEnabled = schoolConfirmed && !busy;
    }

    private static string? OptionalLabel(string value) => value.Trim() is { Length: > 0 } label ? label : null;
}
