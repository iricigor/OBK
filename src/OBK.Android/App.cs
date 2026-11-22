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
                new Setter { Property = Button.BackgroundColorProperty, Value = Color.FromArgb("#0F6CBD") },
                new Setter { Property = Button.TextColorProperty, Value = Colors.White }
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
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(new ReaderPage())
        {
            BarBackgroundColor = Color.FromArgb("#0F6CBD"),
            BarTextColor = Colors.White
        });
}
