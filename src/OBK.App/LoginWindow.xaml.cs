using OBK.Core;
using System.ComponentModel;
using System.Windows;

namespace OBK.App;

public partial class LoginWindow : Window
{
    private readonly MainWindow app;
    private bool completed;
    private bool ready;
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

    private void RememberUnchecked(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        try { app.Store.ForgetCredentials(); }
        catch (ObkException error) { Status.Text = error.Message; Remember.IsChecked = true; }
    }

    public void SetBusy(bool busy)
    {
        SchoolUrl.IsEnabled = Username.IsEnabled = Password.IsEnabled = Remember.IsEnabled =
            FullRefresh.IsEnabled = SignInButton.IsEnabled = OfflineButton.IsEnabled = !busy;
    }

    private async void SignIn(object sender, RoutedEventArgs e)
    {
        if (app.Busy) return;
        try
        {
            var url = KomensClient.NormalizeUrl(SchoolUrl.Text);
            var username = Username.Text.Trim();
            var password = Password.Password;
            if (username.Length == 0 || password.Length == 0) throw new ObkException("Enter both a username and password.");
            if (Remember.IsChecked != true) app.Store.ForgetCredentials();
            Password.Clear();
            SetBusy(true);
            Status.Text = "Signing in, then downloading all three folders...";
            await app.SynchronizeAsync(this, url, username, password, Remember.IsChecked == true, FullRefresh.IsChecked == true);
        }
        catch (ObkException error) { Status.Text = error.Message; SetBusy(false); }
    }

    public void Complete()
    {
        completed = true;
        Password.Clear();
        Close();
    }

    private bool ActivateOffline()
    {
        if (app.Busy) return false;
        try
        {
            var username = Username.Text.Trim();
            if (username.Length > 0) app.ActivateAccount(KomensClient.NormalizeUrl(SchoolUrl.Text), username);
            app.OfflineStatus();
            return true;
        }
        catch (ObkException error) { Status.Text = error.Message; return false; }
    }

    private void ReadOffline(object sender, RoutedEventArgs e)
    {
        if (ActivateOffline()) Complete();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!completed && !ActivateOffline()) { e.Cancel = true; return; }
        Password.Clear();
    }
}
