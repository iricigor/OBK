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
    private readonly Label schoolStatus = new();
    private readonly Label status = new() { Text = AppStrings.SignInPasswordNotice };
    private readonly Button submit = new() { Text = AppStrings.SignInSubmitButton };
    private readonly ActivityIndicator progress = new();
    private bool busy;

    public SignInPage(ReaderSession session, Login? login, Func<Login, Task> saved)
    {
        this.session = session;
        this.saved = saved;
        Title = login is null ? AppStrings.SignInTitleAddChild : AppStrings.SignInTitleSignIn;
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
        var scan = new Button { Text = AppStrings.SignInScanFromPc };
        scan.Clicked += async (_, _) => { if (!busy) await TransferScanPage.OpenAsync(this, Import); };
        var remembered = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { remember, new Label { Text = AppStrings.SignInRememberCredentials, VerticalOptions = LayoutOptions.Center } }
        };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                Children =
                {
                    new Label { Text = AppStrings.SignInHeading, FontSize = 24, FontAttributes = FontAttributes.Bold },
                    new Label { Text = AppStrings.SignInReadOnlyNotice },
                    scan, new Label { Text = AppStrings.SignInScanHint, FontSize = 13 },
                    schoolSearch, schoolStatus, schoolResults,
                    new Label { Text = AppStrings.SignInSchoolAddressLabel }, url,
                    new Label { Text = AppStrings.SignInUsernameLabel }, username,
                    new Label { Text = AppStrings.SignInPasswordLabel }, password,
                    new Label { Text = AppStrings.SignInChildLabel }, child,
                    remembered, submit, progress, status
                }
            }
        };
        ReaderTheme.Apply(this);
    }

    // Fills the form from a PC transfer; the user still reviews it and decides whether to remember the password.
    private void Import(Login login)
    {
        url.Text = login.Url;
        username.Text = login.Username;
        password.Text = login.Password;
        child.Text = login.ChildName;
        remember.IsChecked = false;
        schoolResults.Children.Clear();
        schoolStatus.Text = login.SchoolName ?? "";
        status.Text = AppStrings.SignInImported;
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
                throw new ObkException(AppStrings.ErrorEnterUsernameAndPassword);
            var address = KomensClient.NormalizeUrl(url.Text ?? "");
            var login = new Login(address, user, ChildName: string.IsNullOrWhiteSpace(child.Text) ? null : child.Text.Trim());
            status.Text = AppStrings.StatusSigningIn;
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
            await DisplayAlertAsync(AppStrings.SignInFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
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
