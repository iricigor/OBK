namespace OBK.AndroidApp;

public sealed class App : Application
{
    public App()
    {
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
        var navigation = new NavigationPage(new ReaderPage());
        navigation.SetAppThemeColor(NavigationPage.BarBackgroundColorProperty, Color.FromArgb("#EAEFF5"), Color.FromArgb("#1B2129"));
        navigation.SetAppThemeColor(NavigationPage.BarTextColorProperty, Color.FromArgb("#1A1A1A"), Colors.White);
        return new Window(navigation);
    }
}
