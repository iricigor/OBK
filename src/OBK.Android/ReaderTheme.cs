using Microsoft.Maui.Controls.Shapes;

namespace OBK.AndroidApp;

internal static class ReaderTheme
{
    private static readonly Color Foreground = Color.FromArgb("#1A1A1A");
    private static readonly Color DarkForeground = Colors.White;
    private static readonly Color Secondary = Color.FromArgb("#616161");
    private static readonly Color DarkSecondary = Color.FromArgb("#A6A6A6");
    internal static readonly Color Surface = Colors.White;
    internal static readonly Color DarkSurface = Color.FromArgb("#2B2B2B");
    internal static readonly Color Stroke = Color.FromArgb("#E5E5E5");
    internal static readonly Color DarkStroke = Color.FromArgb("#808080");
    internal static readonly Color Accent = Color.FromArgb("#0F6CBD");
    internal static readonly Color DarkAccent = Color.FromArgb("#4CC2FF");

    public static Label Metadata(Label label)
    {
        if (!label.IsSet(Label.FontSizeProperty)) label.FontSize = 13;
        label.SetAppThemeColor(Label.TextColorProperty, Secondary, DarkSecondary);
        return label;
    }

    public static Button Primary(Button button)
    {
        button.SetAppThemeColor(Button.BackgroundColorProperty, Accent, DarkAccent);
        button.SetAppThemeColor(Button.TextColorProperty, Colors.White, Color.FromArgb("#202020"));
        button.BorderWidth = 0;
        return button;
    }

    public static Button Subtle(Button button)
    {
        button.BackgroundColor = Colors.Transparent;
        button.BorderWidth = 0;
        return button;
    }

    public static Border Panel(View content, Thickness padding)
    {
        var border = new Border
        {
            Content = content,
            Padding = padding,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 }
        };
        border.SetAppThemeColor(VisualElement.BackgroundColorProperty, Surface, DarkSurface);
        border.SetAppTheme<Brush>(Border.StrokeProperty, new SolidColorBrush(Stroke), new SolidColorBrush(DarkStroke));
        return border;
    }

    public static T Apply<T>(T element) where T : VisualElement
    {
        switch (element)
        {
            case ContentPage page:
                page.SetAppThemeColor(VisualElement.BackgroundColorProperty, Color.FromArgb("#F3F3F3"), Color.FromArgb("#202020"));
                break;
            case Label label when !label.IsSet(Label.TextColorProperty):
                label.SetAppThemeColor(Label.TextColorProperty, Foreground, DarkForeground);
                break;
            case Entry entry:
                entry.SetAppThemeColor(Entry.TextColorProperty, Foreground, DarkForeground);
                entry.SetAppThemeColor(Entry.PlaceholderColorProperty, Secondary, DarkSecondary);
                break;
            case Picker picker:
                picker.SetAppThemeColor(Picker.TextColorProperty, Foreground, DarkForeground);
                picker.SetAppThemeColor(Picker.TitleColorProperty, Secondary, DarkSecondary);
                break;
            case SearchBar search:
                search.SetAppThemeColor(SearchBar.TextColorProperty, Foreground, DarkForeground);
                search.SetAppThemeColor(SearchBar.PlaceholderColorProperty, Secondary, DarkSecondary);
                break;
            case Button button:
                if (!button.IsSet(VisualElement.BackgroundColorProperty))
                    button.SetAppThemeColor(VisualElement.BackgroundColorProperty, Surface, DarkSurface);
                if (!button.IsSet(Button.TextColorProperty))
                    button.SetAppThemeColor(Button.TextColorProperty, Accent, DarkAccent);
                if (!button.IsSet(Button.BorderColorProperty))
                    button.SetAppThemeColor(Button.BorderColorProperty, Stroke, DarkStroke);
                break;
            case BoxView line:
                line.SetAppThemeColor(BoxView.ColorProperty, Stroke, DarkStroke);
                break;
        }
        foreach (var child in ((IVisualTreeElement)element).GetVisualChildren().OfType<VisualElement>())
            Apply(child);
        return element;
    }
}
