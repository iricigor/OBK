using OBK.AndroidApp.Localization;
using OBK.Core;

namespace OBK.AndroidApp;

internal sealed class AccountPickerPage : ContentPage
{
    internal bool Busy { get; private set; }

    internal AccountPickerPage(IReadOnlyList<Login> known, Login? active, Func<Login, Task> select)
    {
        Title = AppStrings.ReaderAccountPickerTitle;
        LanguageToolbar.Add(this);
        var accounts = new VerticalStackLayout { Padding = 20, Spacing = 8 };
        foreach (var login in known)
        {
            accounts.Add(ReaderTheme.AccountChoice(login, known, active, async (_, _) =>
            {
                if (Busy) return;
                Busy = true;
                accounts.IsEnabled = false;
                try { await select(login); }
                finally
                {
                    accounts.IsEnabled = true;
                    Busy = false;
                }
            }));
            accounts.Add(new BoxView { HeightRequest = 1 });
        }
        Content = new ScrollView { Content = accounts };
        ReaderTheme.Apply(this);
        this.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Surface, ReaderTheme.DarkSurface);
    }
}
