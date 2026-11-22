using OBK.Core;

namespace OBK.AndroidApp;

internal sealed class SignInPage : ContentPage
{
    private readonly ReaderSession session;
    private readonly Func<Login, Task> saved;
    private readonly Entry url = new() { Placeholder = "https://your-school.bakalari.cz", Keyboard = Keyboard.Url };
    private readonly Entry username = new() { Placeholder = "Komens username", IsTextPredictionEnabled = false };
    private readonly Entry password = new() { Placeholder = "Password", IsPassword = true, IsTextPredictionEnabled = false };
    private readonly Entry child = new() { Placeholder = "Child's name (optional)" };
    private readonly CheckBox remember = new() { MinimumHeightRequest = 48, MinimumWidthRequest = 48 };
    private readonly SearchBar schoolSearch = new() { Placeholder = "Search school or town" };
    private readonly VerticalStackLayout schoolResults = new() { Spacing = 8 };
    private readonly Label schoolStatus = new();
    private readonly Label status = new() { Text = "Your password is saved only if you choose Remember credentials." };
    private readonly Button submit = new() { Text = "Sign in and download" };
    private readonly ActivityIndicator progress = new();
    private bool busy;

    public SignInPage(ReaderSession session, Login? login, Func<Login, Task> saved)
    {
        this.session = session;
        this.saved = saved;
        Title = login is null ? "Add child" : "Sign in";
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
                found.Total == 0 ? "No matching school. You can enter its HTTPS address below." :
                found.Total > 6 ? $"{found.Total} schools match. Type more to narrow the results." :
                $"{found.Total} matching schools";
            if (found.Total > 6) return;
            foreach (var school in found.Visible)
            {
                var choose = new Button { Text = $"{school.Name}\n{school.Towns}", LineBreakMode = LineBreakMode.WordWrap };
                choose.Clicked += (_, _) =>
                {
                    url.Text = school.SchoolUrl;
                    schoolResults.Children.Clear();
                    schoolStatus.Text = school.Name;
                };
                schoolResults.Add(choose);
            }
        };
        submit.Clicked += async (_, _) => await SignInAsync();
        var remembered = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { remember, new Label { Text = "Remember credentials", VerticalOptions = LayoutOptions.Center } }
        };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Download once. Read offline.", FontSize = 24, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "OBK only reads messages. It never sends replies, confirmations or read receipts." },
                    schoolSearch, schoolStatus, schoolResults,
                    new Label { Text = "School HTTPS address" }, url,
                    new Label { Text = "Username" }, username,
                    new Label { Text = "Password" }, password,
                    new Label { Text = "Child's name" }, child,
                    remembered, submit, progress, status
                }
            }
        };
        ReaderTheme.Apply(this);
    }

    private async Task SignInAsync()
    {
        if (busy) return;
        busy = true;
        submit.IsEnabled = false;
        progress.IsRunning = true;
        schoolSearch.IsEnabled = url.IsEnabled = username.IsEnabled = password.IsEnabled = child.IsEnabled = remember.IsEnabled = false;
        schoolResults.IsEnabled = false;
        try
        {
            var user = username.Text?.Trim() ?? "";
            var secret = password.Text ?? "";
            if (user.Length == 0 || secret.Length == 0)
                throw new ObkException("Enter your school username and password.");
            var address = KomensClient.NormalizeUrl(url.Text ?? "");
            var login = new Login(address, user, ChildName: string.IsNullOrWhiteSpace(child.Text) ? null : child.Text.Trim());
            status.Text = "Signing in...";
            await session.SynchronizeAsync(login, secret, remember.IsChecked,
                new Progress<string>(text => status.Text = text));
            password.Text = "";
            if (Navigation.NavigationStack.LastOrDefault() == this)
                await Navigation.PopAsync();
            await saved(login);
        }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            status.Text = error.Message;
            await DisplayAlertAsync("Sign-in or download failed", error.Message, "OK");
        }
        finally
        {
            busy = false;
            submit.IsEnabled = true;
            progress.IsRunning = false;
            schoolSearch.IsEnabled = url.IsEnabled = username.IsEnabled = password.IsEnabled = child.IsEnabled = remember.IsEnabled = true;
            schoolResults.IsEnabled = true;
        }
    }
}
