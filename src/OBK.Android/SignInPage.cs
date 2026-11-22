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
    private readonly ActivityIndicator progress = new() { IsVisible = false };
    private bool busy;

    public SignInPage(ReaderSession session, Login? login, Func<Login, Task> saved)
    {
        this.session = session;
        this.saved = saved;
        Title = login is null ? AppStrings.SignInTitleAddChild : AppStrings.SignInTitleSignIn;
        var addressFields = Field(AppStrings.SignInSchoolAddressLabel, url);
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
        var remembered = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = 8
        };
        remembered.Add(remember, 0);
        remembered.Add(new Label { Text = AppStrings.SignInRememberCredentials, FontSize = 14, VerticalOptions = LayoutOptions.Center }, 1);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 12,
                Children =
                {
                    new Label { Text = AppStrings.SignInHeading, FontSize = 20, FontAttributes = FontAttributes.Bold },
                    schoolSearch, schoolStatus, schoolResults,
                    manualAddress, addressFields,
                    Field(AppStrings.SignInUsernameLabel, username),
                    Field(AppStrings.SignInPasswordLabel, password),
                    Field(AppStrings.SignInChildLabel, child),
                    remembered, submit, progress, status,
                    ReaderTheme.Metadata(new Label { Text = AppStrings.SignInReadOnlyNotice })
                }
            }
        };
        ReaderTheme.Apply(this);
    }

    private static VerticalStackLayout Field(string label, View input) => new()
    {
        Spacing = 4,
        Children = { ReaderTheme.Metadata(new Label { Text = label }), ReaderTheme.Panel(input, new Thickness(8, 0)) }
    };

    private async Task SignInAsync()
    {
        if (busy) return;
        busy = true;
        submit.IsEnabled = false;
        progress.IsRunning = true;
        progress.IsVisible = true;
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
            progress.IsVisible = false;
            manualAddress.IsEnabled = true;
            schoolSearch.IsEnabled = url.IsEnabled = username.IsEnabled = password.IsEnabled = child.IsEnabled = remember.IsEnabled = true;
            schoolResults.IsEnabled = true;
        }
    }
}
