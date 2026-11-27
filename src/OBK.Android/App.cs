using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

public sealed class App : Application
{
    private bool switchingLanguage;
    private static NotificationDestination? notificationRequested;
    private static string LanguagePath => Path.Combine(FileSystem.AppDataDirectory, "language.json");
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
        ReaderPreferences.Initialize();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window();
        window.Created += async (_, _) =>
        {
            var failure = await ApplySavedLanguageAsync();
            var surface = BuildNavigation();
            window.Page = surface.Shell;
            try { ScheduledCheck.Schedule(ReaderPreferences.ScheduledChecks); }
            catch (ObkException error)
            {
                await surface.Reader.DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
            }
            if (failure is not null)
                await surface.Reader.DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, failure, AppStrings.AlertOkButton);
        };
        window.Page = new ContentPage { Content = new ActivityIndicator { IsRunning = true } };
        return window;
    }

    /// <summary>Applies the saved UI language, or the device language; returns a load failure to show, if any.</summary>
    internal static async Task<string?> ApplySavedLanguageAsync()
    {
        var osLanguage = Java.Util.Locale.Default?.ToLanguageTag() ?? "und";
        var language = LanguageSettings.Detect(osLanguage);
        string? failure = null;
        try { language = await LanguageSettings.LoadAsync(LanguagePath, osLanguage); }
        catch (ObkException error) { failure = error.Message; }
        LanguageSettings.Apply(language);
        AppStrings.Culture = LanguageSettings.Culture;
        return failure;
    }

    private ReaderPage? CurrentReader =>
        Windows.FirstOrDefault()?.Page is FlyoutPage { Detail: NavigationPage { RootPage: ReaderPage reader } } ? reader : null;

    /// <summary>Remembers a tapped check notification until the offline reader is ready.</summary>
    internal static void RequestNotification(NotificationDestination destination) => notificationRequested = destination;

    internal static NotificationDestination? TakeNotificationRequest()
    {
        var requested = notificationRequested;
        notificationRequested = null;
        return requested;
    }

    internal Task ShowRequestedNotificationAsync() => CurrentReader?.ShowNotificationAsync() ?? Task.CompletedTask;

    /// <summary>A scheduled check saved new messages; a running reader shows them without contacting the school.</summary>
    internal Task ArchivesChangedAsync() => CurrentReader?.ReloadFromStoreAsync() ?? Task.CompletedTask;

    internal async Task SwitchLanguageAsync(ContentPage source)
    {
        if (switchingLanguage) throw new ObkException(SharedStrings.ErrorLanguageBusy);
        var stack = source.Navigation.NavigationStack;
        if (stack.FirstOrDefault() is not ReaderPage reader ||
            reader.Busy || reader.InitialDownloadRunning || stack.OfType<SignInPage>().Any(page => page.Busy) ||
            stack.OfType<MessagePage>().Any(page => page.Busy) ||
            stack.OfType<SettingsPage>().Any(page => page.Busy) ||
            stack.OfType<AccountsPage>().Any(page => page.Busy) ||
            stack.OfType<AccountPickerPage>().Any(page => page.Busy) ||
            stack.OfType<MarksDetailPage>().Any(page => page.Busy) ||
            stack.Skip(1).Any(page => page is not (SignInPage or MessagePage or SettingsPage or AccountsPage or AccountPickerPage or MarksPage or MarksDetailPage)))
            throw new ObkException(SharedStrings.ErrorLanguageBusy);
        switchingLanguage = true;
        try
        {
            var next = LanguageSettings.NextCode;
            await LanguageSettings.SaveAsync(LanguagePath, next);
            LanguageSettings.Apply(next);
            AppStrings.Culture = LanguageSettings.Culture;
            var surface = BuildNavigation(reader);
            foreach (var page in stack.Skip(1))
            {
                Page replacement = page switch
                {
                    SignInPage signIn => signIn.Rebuild(surface.Reader.AccountSavedAsync),
                    MessagePage message => message.Rebuild(surface.Reader.OpenRelatedAsync),
                    SettingsPage => surface.Reader.CreateSettingsPage(),
                    AccountsPage => surface.Reader.CreateAccountsPage(),
                    AccountPickerPage => surface.Reader.CreateAccountPickerPage(),
                    MarksPage marks => marks.Rebuild(surface.Reader),
                    MarksDetailPage detail => detail.Rebuild(),
                    _ => throw new ObkException(SharedStrings.ErrorLanguageBusy)
                };
                await surface.Navigation.PushAsync(replacement, animated: false);
            }
            source.Window!.Page = surface.Shell;
        }
        finally { switchingLanguage = false; }
    }

    private static (ReaderNavigation Shell, NavigationPage Navigation, ReaderPage Reader) BuildNavigation(ReaderPage? previous = null)
    {
        var flyout = new ReaderNavigation { FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover };
        var reader = previous?.Rebuild(() => flyout.IsPresented = true) ?? new ReaderPage(() => flyout.IsPresented = true);
        var navigation = new NavigationPage(reader);
        navigation.SetAppThemeColor(NavigationPage.BarBackgroundColorProperty, Color.FromArgb("#EAEFF5"), Color.FromArgb("#1B2129"));
        navigation.SetAppThemeColor(NavigationPage.BarTextColorProperty, Color.FromArgb("#1A1A1A"), Colors.White);
        var settings = ReaderTheme.StartAlignedButton(new Button { Text = AppStrings.SettingsPageTitle });
        settings.Clicked += async (_, _) =>
        {
            flyout.IsPresented = false;
            await reader.OpenSettingsAsync();
        };
        var accounts = ReaderTheme.StartAlignedButton(new Button { Text = AppStrings.SettingsManageAccounts });
        accounts.Clicked += async (_, _) =>
        {
            flyout.IsPresented = false;
            await reader.AccountActionsAsync();
        };
        var children = new VerticalStackLayout { Spacing = 0 };
        var addChild = ReaderTheme.StartAlignedButton(new Button
        {
            Text = AppStrings.ReaderAccountsSheetAddChild,
            ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 12)
        });
        addChild.SetAppTheme<ImageSource>(Button.ImageSourceProperty,
            ImageSource.FromFile("add.png"), ImageSource.FromFile("add_dark.png"));
        addChild.Clicked += async (_, _) =>
        {
            flyout.IsPresented = false;
            await reader.AddAccountAsync();
        };
        var folders = new VerticalStackLayout { Spacing = 0 };
        var marks = ReaderTheme.StartAlignedButton(new Button
        {
            Text = SharedStrings.MarksTitle,
            ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 12)
        });
        marks.SetAppTheme<ImageSource>(Button.ImageSourceProperty,
            ImageSource.FromFile("marks.png"), ImageSource.FromFile("marks_dark.png"));
        marks.Clicked += async (_, _) =>
        {
            flyout.IsPresented = false;
            await reader.OpenMarksAsync();
        };
        var folderButtons = new List<Button>();
        var folderBadges = new List<Grid>();
        foreach (var index in Enumerable.Range(0, reader.FolderLabels.Count))
        {
            var button = ReaderTheme.StartAlignedButton(new Button
            {
                Text = reader.FolderLabels[index],
                ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 12)
            });
            button.SetAppTheme<ImageSource>(Button.ImageSourceProperty,
                ImageSource.FromFile($"{ReaderPage.FolderIcons[index]}.png"),
                ImageSource.FromFile($"{ReaderPage.FolderIcons[index]}_dark.png"));
            button.Clicked += async (_, _) =>
            {
                if (reader.Busy) return;
                reader.SelectFolder(index);
                flyout.IsPresented = false;
                await navigation.PopToRootAsync();
            };
            var badge = new Grid { InputTransparent = true, HorizontalOptions = LayoutOptions.End };
            folders.Add(new Grid { Children = { button, badge } });
            folderButtons.Add(button);
            folderBadges.Add(badge);
        }
        flyout.IsPresentedChanged += (_, _) =>
        {
            if (!flyout.IsPresented) return;
            accounts.IsEnabled = reader.AccountsAvailable;
            addChild.IsEnabled = reader.AccountsAvailable;
            children.Children.Clear();
            if (reader.KnownAccounts.Count > 1)
                children.Add(ReaderTheme.AllChildrenChoice(reader.KnownAccounts, reader.ShowingAllChildren, reader.NewCountAll, async (_, _) =>
                {
                    if (reader.Busy) return;
                    flyout.IsPresented = false;
                    await reader.SelectAllChildrenAsync();
                    if (reader.CurrentMarks is not null) await reader.OpenMarksAsync();
                }));
            foreach (var login in reader.KnownAccounts)
                children.Add(ReaderTheme.AccountChoice(login, reader.KnownAccounts, reader.ActiveAccount, async (_, _) =>
                {
                    if (reader.Busy) return;
                    flyout.IsPresented = false;
                    await reader.SelectAccountAsync(login);
                    if (reader.CurrentMarks is not null) await reader.OpenMarksAsync();
                }, compact: true, newCount: reader.NewCount(login)));
            children.IsEnabled = reader.AccountsAvailable;
            marks.IsEnabled = reader.AccountsAvailable;
            marks.FontAttributes = reader.CurrentMarks is not null ? FontAttributes.Bold : FontAttributes.None;
            for (var index = 0; index < folderButtons.Count; index++)
            {
                var button = folderButtons[index];
                var count = reader.FolderNewCount(index);
                folderBadges[index].Children.Clear();
                folderBadges[index].Add(ReaderTheme.Badge(count));
                button.IsEnabled = !reader.Busy;
                var selectedFolder = reader.CurrentMarks is null && index == reader.FolderIndex;
                button.FontAttributes = selectedFolder ? FontAttributes.Bold : FontAttributes.None;
                button.SetAppThemeColor(Button.BackgroundColorProperty,
                    selectedFolder ? ReaderTheme.Selection : Colors.Transparent,
                    selectedFolder ? ReaderTheme.DarkSelection : Colors.Transparent);
                var description = selectedFolder ?
                    Localized.Format(AppStrings.ReaderSelectedFolder, reader.FolderLabels[index]) : reader.FolderLabels[index];
                SemanticProperties.SetDescription(button, count > 0
                    ? description + ", " + Localized.Format(AppStrings.ReaderNewCount, count) : description);
            }
        };
        flyout.Flyout = ReaderTheme.Apply(new ContentPage
        {
            Title = "OBK",
            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(12, 14), Spacing = 8,
                    Children =
                    {
                        new VerticalStackLayout
                        {
                            Spacing = 3,
                            Children =
                            {
                                new Label { Text = "OBK", FontSize = 20, FontAttributes = FontAttributes.Bold },
                                ReaderTheme.Metadata(new Label { Text = "Offline Bakalari Komens", FontSize = 12 })
                            }
                        },
                        ReaderTheme.Metadata(new Label { Text = AppStrings.ReaderDrawerChildren, FontSize = 12, FontAttributes = FontAttributes.Bold }),
                        children, addChild, new BoxView { HeightRequest = 1 },
                        folders, marks, new BoxView { HeightRequest = 1 }, accounts, settings
                    }
                }
            }
        });
        flyout.Detail = navigation;
        return (flyout, navigation, reader);
    }

    private sealed class ReaderNavigation : FlyoutPage, IFlyoutView
    {
        double IFlyoutView.FlyoutWidth => 280;

        protected override bool OnBackButtonPressed()
        {
            if (!IsPresented) return base.OnBackButtonPressed();
            IsPresented = false;
            return true;
        }
    }
}
