namespace OBK.AndroidApp;

internal static class ReaderTheme
{
    private static readonly Color Foreground = Color.FromArgb("#1A1A1A");
    private static readonly Color DarkForeground = Color.FromArgb("#F3F3F3");
    private static readonly Color Secondary = Color.FromArgb("#616161");
    private static readonly Color DarkSecondary = Color.FromArgb("#C4C4C4");

    public static T Apply<T>(T element) where T : VisualElement
    {
        switch (element)
        {
            case ContentPage page:
                page.SetAppThemeColor(VisualElement.BackgroundColorProperty, Colors.White, Color.FromArgb("#121212"));
                break;
            case Label label:
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
        }
        foreach (var child in ((IVisualTreeElement)element).GetVisualChildren().OfType<VisualElement>())
            Apply(child);
        return element;
    }
}
