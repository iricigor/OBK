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
            try { await ((App)Application.Current!).SwitchLanguageAsync(page); }
            catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
            {
                await page.DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
            }
            finally { item.IsEnabled = true; }
        };
        page.ToolbarItems.Add(item);
    }
}
