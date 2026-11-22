using OBK.AndroidApp.Localization;

namespace OBK.AndroidApp;

public sealed class App : Application
{
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
        var flyout = new ReaderNavigation { FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover };
        var reader = new ReaderPage(() => flyout.IsPresented = true);
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
        return new Window(flyout);
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
