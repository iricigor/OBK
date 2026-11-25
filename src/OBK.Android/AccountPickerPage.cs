using OBK.AndroidApp.Localization;
using OBK.Core;

namespace OBK.AndroidApp;

internal sealed class AccountPickerPage : ContentPage
{
    internal bool Busy { get; private set; }

    internal AccountPickerPage(IReadOnlyList<Login> known, Login? active, bool allActive, Func<Login?, Task> select)
    {
        Title = AppStrings.ReaderAccountPickerTitle;
        LanguageToolbar.Add(this);
        var accounts = new VerticalStackLayout { Padding = 20, Spacing = 8 };
        async Task Choose(Login? login)
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
        }
        if (known.Count > 1)
        {
            accounts.Add(ReaderTheme.AllChildrenChoice(known, allActive, 0, async (_, _) => await Choose(null)));
            accounts.Add(new BoxView { HeightRequest = 1 });
        }
        foreach (var login in known)
        {
            accounts.Add(ReaderTheme.AccountChoice(login, known, active, async (_, _) => await Choose(login)));
            accounts.Add(new BoxView { HeightRequest = 1 });
        }
        Content = new ScrollView { Content = accounts };
        ReaderTheme.Apply(this);
        this.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Surface, ReaderTheme.DarkSurface);
    }
}
