using OBK.Core;
using System.ComponentModel;
using System.Windows;

namespace OBK.App;

public partial class LoginWindow : Window
{
    private readonly MainWindow app;
    private readonly Login? initial;
    private bool completed;
    private bool ready;
    private bool busy;
    private bool manualUrl;
    private bool updatingSearch;
    private bool updatingUrl;
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
        selectedSchoolName = initial?.SchoolName;
        Remember.IsChecked = remembered;
        Status.Text = error;
        ready = true;
        DirectoryReady = LoadSchoolDirectoryAsync();
        Loaded += async (_, _) =>
        {
            await DirectoryReady;
            if (manualUrl) SchoolUrl.Focus();
            else if (SchoolSearch.IsEnabled) SchoolSearch.Focus();
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
        SchoolMatches.IsEnabled = SchoolUrl.IsEnabled = Username.IsEnabled = ChildName.IsEnabled =
            Password.IsEnabled = Remember.IsEnabled = FullRefresh.IsEnabled =
            SignInButton.IsEnabled = OfflineButton.IsEnabled = !busy;
    }

    private async void SignIn(object sender, RoutedEventArgs e)
    {
        if (app.Busy || busy) return;
        try
        {
            var url = KomensClient.NormalizeUrl(SchoolUrl.Text);
            var username = Username.Text.Trim();
            var password = Password.Password;
            if (username.Length == 0 || password.Length == 0) throw new ObkException("Enter both a username and password.");
            var remember = Remember.IsChecked == true;
            var fullRefresh = FullRefresh.IsChecked == true;
            var childName = OptionalLabel(ChildName.Text);
            var schoolName = selectedSchoolName;
            Password.Clear();
            SetBusy(true);
            if (!remember) await Task.Run(app.Store.ForgetCredentials);
            Status.Text = "Signing in, then downloading all three folders...";
            await app.SynchronizeAsync(this, url, username, password, remember, fullRefresh, childName, schoolName);
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
            var username = Username.Text.Trim();
            if (username.Length > 0)
            {
                var url = KomensClient.NormalizeUrl(SchoolUrl.Text);
                var childName = OptionalLabel(ChildName.Text);
                var schoolName = selectedSchoolName;
                await Task.Run(() => app.Store.SaveAccount(url, username, childName, schoolName));
                await app.ActivateAccountAsync(url, username, childName, schoolName);
            }
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

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (completed) { Password.Clear(); return; }
        e.Cancel = true;
        if (busy || app.Busy) { Status.Text = "Wait for the current operation to finish."; return; }
        SetBusy(true);
        try { if (await ActivateOfflineAsync()) Complete(); }
        finally { SetBusy(false); }
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
                    throw new ObkException("The bundled school directory is unavailable. Enter the school URL manually.");
                return SchoolDirectory.Load(stream);
            });
            schoolDirectory = loaded;
            var match = initial?.Url is { Length: > 0 } url ? loaded.FindByUrl(url) : null;
            if (match is not null)
            {
                selectedSchoolName = match.Name;
                updatingUrl = true;
                SchoolUrl.Text = match.SchoolUrl;
                updatingUrl = false;
                updatingSearch = true;
                SchoolSearch.Text = match.Name;
                updatingSearch = false;
                UpdateSchoolSearch();
                SchoolMatches.SelectedItem = match;
                SearchSelected(match);
            }
            else if (initial?.Url is { Length: > 0 })
            {
                ShowManualUrl();
                SchoolSearchStatus.Text = $"{loaded.Count:N0} bundled schools are available.";
            }
            else UpdateSchoolSearch();
            SchoolSearch.IsEnabled = true;
        }
        catch (ObkException error)
        {
            SchoolSearchStatus.Text = error.Message;
            ShowManualUrl();
            ManualUrlButton.IsEnabled = false;
            ManualUrlButton.Content = "School search unavailable";
        }
    }

    private void SchoolSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (updatingSearch || schoolDirectory is null) return;
        selectedSchoolName = null;
        updatingUrl = true;
        SchoolUrl.Text = "";
        updatingUrl = false;
        UpdateSchoolSearch();
    }

    private void SchoolUrlChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!updatingUrl) selectedSchoolName = null;
    }

    private void SchoolSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SchoolMatches.SelectedItem is SchoolDirectoryEntry school) SearchSelected(school);
    }

    private void SearchSelected(SchoolDirectoryEntry school)
    {
        selectedSchoolName = school.Name;
        updatingUrl = true;
        SchoolUrl.Text = school.SchoolUrl;
        updatingUrl = false;
        SchoolSearchStatus.Text = $"Selected {school.Name}. You can continue typing to choose another school.";
    }

    private void UpdateSchoolSearch()
    {
        if (schoolDirectory is null) return;
        var query = SchoolSearch.Text.Trim();
        if (query.Length == 0)
        {
            SchoolMatches.ItemsSource = null;
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = $"Search {schoolDirectory.Count:N0} bundled schools by name, town, or URL.";
            return;
        }
        var result = schoolDirectory.Search(query);
        SchoolMatches.ItemsSource = result.Visible;
        if (result.Total == 0)
        {
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = "No schools found. Try a different name, town, or URL, or enter the URL manually.";
        }
        else if (result.Total > result.Visible.Count)
        {
            SchoolMatches.Visibility = Visibility.Collapsed;
            SchoolSearchStatus.Text = $"{result.Total:N0} schools match. Keep typing to narrow the list below 7.";
        }
        else
        {
            SchoolMatches.Visibility = Visibility.Visible;
            SchoolSearchStatus.Text = $"{result.Total} {(result.Total == 1 ? "school matches" : "schools match")}. Select one.";
        }
    }

    private void ToggleManualUrl(object sender, RoutedEventArgs e)
    {
        ShowManualUrl(!manualUrl);
        if (manualUrl) SchoolUrl.Focus();
        else SchoolSearch.Focus();
    }

    private void ShowManualUrl(bool manual = true)
    {
        manualUrl = manual;
        SchoolSearchPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        ManualUrlPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ManualUrlButton.Content = manual ? "Find school by name" : "Enter URL manually";
    }

    private static string? OptionalLabel(string value) => value.Trim() is { Length: > 0 } label ? label : null;
}
