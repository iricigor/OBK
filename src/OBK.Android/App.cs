using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

public sealed class App : Application
{
    private bool switchingLanguage;
    private static string LanguagePath => Path.Combine(FileSystem.AppDataDirectory, "language.json");
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
            var reader = new ReaderPage();
            window.Page = Navigation(reader);
            if (failure is not null)
                await reader.DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, failure, AppStrings.AlertOkButton);
        };
        window.Page = new ContentPage { Content = new ActivityIndicator { IsRunning = true } };
        return window;
    }

    private static NavigationPage Navigation(Page root) => new(root)
    {
        BarBackgroundColor = Color.FromArgb("#0F6CBD"),
        BarTextColor = Colors.White
    };

    internal async Task SwitchLanguageAsync(ContentPage source)
    {
        if (switchingLanguage) throw new ObkException(SharedStrings.ErrorLanguageBusy);
        var stack = source.Navigation.NavigationStack;
        if (stack.FirstOrDefault() is not ReaderPage reader ||
            reader.Busy || stack.OfType<SignInPage>().Any(page => page.Busy) ||
            stack.OfType<MessagePage>().Any(page => page.Busy) ||
            stack.Skip(1).Any(page => page is not (SignInPage or MessagePage)))
            throw new ObkException(SharedStrings.ErrorLanguageBusy);
        switchingLanguage = true;
        try
        {
            var next = LanguageSettings.NextCode;
            await LanguageSettings.SaveAsync(LanguagePath, next);
            LanguageSettings.Apply(next);
            AppStrings.Culture = LanguageSettings.Culture;
            var rebuiltReader = reader.Rebuild();
            var navigation = Navigation(rebuiltReader);
            foreach (var page in stack.Skip(1))
            {
                Page replacement = page switch
                {
                    SignInPage signIn => signIn.Rebuild(rebuiltReader.AccountSavedAsync),
                    MessagePage message => message.Rebuild(),
                    _ => throw new ObkException(SharedStrings.ErrorLanguageBusy)
                };
                await navigation.PushAsync(replacement, animated: false);
            }
            source.Window!.Page = navigation;
        }
        finally { switchingLanguage = false; }
    }
}
