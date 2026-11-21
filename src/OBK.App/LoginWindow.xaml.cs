using OBK.Core;
using System.ComponentModel;
using System.Windows;

namespace OBK.App;

public partial class LoginWindow : Window
{
    private readonly MainWindow app;
    private bool completed;
    private bool ready;
    private bool busy;
    public string StatusText { set => Status.Text = value; }

    public LoginWindow(MainWindow app, Login? initial, bool remembered, string error)
    {
        this.app = app;
        Owner = app;
        InitializeComponent();
        SchoolUrl.Text = initial?.Url ?? KomensClient.DefaultUrl;
        Username.Text = initial?.Username ?? "";
        Password.Password = initial?.Password ?? "";
        Remember.IsChecked = remembered;
        Status.Text = error;
        ready = true;
        Loaded += (_, _) => { if (Username.Text.Length > 0) Password.Focus(); else Username.Focus(); };
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
        SchoolUrl.IsEnabled = Username.IsEnabled = Password.IsEnabled = Remember.IsEnabled =
            FullRefresh.IsEnabled = SignInButton.IsEnabled = OfflineButton.IsEnabled = !busy;
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
            Password.Clear();
            SetBusy(true);
            if (!remember) await Task.Run(app.Store.ForgetCredentials);
            Status.Text = "Signing in, then downloading all three folders...";
            await app.SynchronizeAsync(this, url, username, password, remember, fullRefresh);
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
            if (username.Length > 0) await app.ActivateAccountAsync(KomensClient.NormalizeUrl(SchoolUrl.Text), username);
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
}
