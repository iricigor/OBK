using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

public sealed class App : Application
{
    private bool switchingLanguage;
    private static string LanguagePath => Path.Combine(FileSystem.AppDataDirectory, "language.json");
    internal const string AppearancePreference = "reader_appearance";
    public App()
    {
        UserAppTheme = Preferences.Default.Get(AppearancePreference, nameof(AppTheme.Unspecified)) switch
        {
            nameof(AppTheme.Unspecified) => AppTheme.Unspecified,
            nameof(AppTheme.Light) => AppTheme.Light,
            nameof(AppTheme.Dark) => AppTheme.Dark,
            _ => throw new InvalidOperationException("Unknown saved appearance preference.")
        };
        Resources.Add(new Style(typeof(Button))
        {
            Setters =
            {
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 48d },
                new Setter { Property = Button.FontSizeProperty, Value = 14d },
                new Setter { Property = Button.PaddingProperty, Value = new Thickness(12, 8) },
                new Setter { Property = Button.CornerRadiusProperty, Value = 6 },
                new Setter { Property = Button.BorderWidthProperty, Value = 1d },
            }
        });
        Resources.Add(new Style(typeof(Entry))
        {
            Setters = { new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 48d } }
        });
        Resources.Add(new Style(typeof(Picker))
        {
            Setters = { new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 48d } }
        });
        Resources.Add(new Style(typeof(SearchBar))
        {
            Setters = { new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 48d } }
        });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window();
        window.Created += async (_, _) =>
        {
            var osLanguage = Java.Util.Locale.Default?.ToLanguageTag() ?? "und";
            var language = LanguageSettings.Detect(osLanguage);
            string? failure = null;
            try { language = await LanguageSettings.LoadAsync(LanguagePath, osLanguage); }
            catch (ObkException error) { failure = error.Message; }
            LanguageSettings.Apply(language);
            AppStrings.Culture = LanguageSettings.Culture;
            var surface = BuildNavigation();
            window.Page = surface.Shell;
            if (failure is not null)
                await surface.Reader.DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, failure, AppStrings.AlertOkButton);
        };
        window.Page = new ContentPage { Content = new ActivityIndicator { IsRunning = true } };
        return window;
    }

    internal async Task SwitchLanguageAsync(ContentPage source)
    {
        if (switchingLanguage) throw new ObkException(SharedStrings.ErrorLanguageBusy);
        var stack = source.Navigation.NavigationStack;
        if (stack.FirstOrDefault() is not ReaderPage reader ||
            reader.Busy || stack.OfType<SignInPage>().Any(page => page.Busy) ||
            stack.OfType<MessagePage>().Any(page => page.Busy) ||
            stack.OfType<SettingsPage>().Any(page => page.Busy) ||
            stack.Skip(1).Any(page => page is not (SignInPage or MessagePage or SettingsPage)))
            throw new ObkException(SharedStrings.ErrorLanguageBusy);
        switchingLanguage = true;
        try
        {
            var next = LanguageSettings.NextCode;
            await LanguageSettings.SaveAsync(LanguagePath, next);
            LanguageSettings.Apply(next);
            AppStrings.Culture = LanguageSettings.Culture;
            var surface = BuildNavigation(reader);
            foreach (var page in stack.Skip(1))
            {
                Page replacement = page switch
                {
                    SignInPage signIn => signIn.Rebuild(surface.Reader.AccountSavedAsync),
                    MessagePage message => message.Rebuild(),
                    SettingsPage => surface.Reader.CreateSettingsPage(),
                    _ => throw new ObkException(SharedStrings.ErrorLanguageBusy)
                };
                await surface.Navigation.PushAsync(replacement, animated: false);
            }
            source.Window!.Page = surface.Shell;
        }
        finally { switchingLanguage = false; }
    }

    private static (ReaderNavigation Shell, NavigationPage Navigation, ReaderPage Reader) BuildNavigation(ReaderPage? previous = null)
    {
        var flyout = new ReaderNavigation { FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover };
        var reader = previous?.Rebuild(() => flyout.IsPresented = true) ?? new ReaderPage(() => flyout.IsPresented = true);
        var navigation = new NavigationPage(reader);
        navigation.SetAppThemeColor(NavigationPage.BarBackgroundColorProperty, Color.FromArgb("#EAEFF5"), Color.FromArgb("#1B2129"));
        navigation.SetAppThemeColor(NavigationPage.BarTextColorProperty, Color.FromArgb("#1A1A1A"), Colors.White);
        var settings = ReaderTheme.Subtle(new Button { Text = AppStrings.SettingsPageTitle, HorizontalOptions = LayoutOptions.Start });
        settings.Clicked += async (_, _) =>
        {
            flyout.IsPresented = false;
            await reader.OpenSettingsAsync();
        };
        flyout.Flyout = ReaderTheme.Apply(new ContentPage
        {
            Title = "OBK",
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 24), Spacing = 24,
                Children = { new Label { Text = "OBK", FontSize = 24, FontAttributes = FontAttributes.Bold }, settings }
            }
        });
        flyout.Detail = navigation;
        return (flyout, navigation, reader);
    }

    private sealed class ReaderNavigation : FlyoutPage
    {
        protected override bool OnBackButtonPressed()
        {
            if (!IsPresented) return base.OnBackButtonPressed();
            IsPresented = false;
            return true;
        }
    }
}
