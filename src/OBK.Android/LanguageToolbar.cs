using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal static class LanguageToolbar
{
    public static void Add(ContentPage page)
    {
        var item = new ToolbarItem { Text = LanguageSettings.Code.ToUpperInvariant(), Order = ToolbarItemOrder.Primary };
        ToolTipProperties.SetText(item, LanguageSettings.SwitchTooltip);
        SemanticProperties.SetDescription(item, LanguageSettings.SwitchTooltip);
        item.Clicked += async (_, _) =>
        {
            item.IsEnabled = false;
            try { await SwitchAsync(page); }
            finally { item.IsEnabled = true; }
        };
        page.ToolbarItems.Add(item);
    }

    public static Button CreateButton(ContentPage page)
    {
        var button = ReaderTheme.Subtle(new Button
        {
            Text = LanguageSettings.Code.ToUpperInvariant(), FontSize = 12,
            WidthRequest = 48, HeightRequest = 48, MinimumWidthRequest = 48, Padding = 0
        });
        ToolTipProperties.SetText(button, LanguageSettings.SwitchTooltip);
        SemanticProperties.SetDescription(button, LanguageSettings.SwitchTooltip);
        button.Clicked += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await SwitchAsync(page); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static async Task SwitchAsync(ContentPage page)
    {
        try { await ((App)Application.Current!).SwitchLanguageAsync(page); }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            await page.DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
        }
    }
}
