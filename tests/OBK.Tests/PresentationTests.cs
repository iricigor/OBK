using OBK.App;
using OBK.Core;
using OBK.Core.Localization;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace OBK.Tests;

[Collection("Language")]
public sealed class PresentationTests
{
    [Theory]
    [InlineData("true", "2026-11-22T14:03:04Z", true)]
    [InlineData("true", "2026-11-22T16:03:04.1234567+02:00", true)]
    [InlineData("false", "2026-11-22T14:03:04.1234567Z", false)]
    public void BuildIdentityUsesExplicitOriginAndNormalizesUtc(string origin, string timestamp, bool local)
    {
        var build = BuildIdentity.FromMetadata(origin, timestamp);
        Assert.Equal(local, build.IsLocal);
        Assert.Equal("2026-11-22 14:03 UTC", build.TimestampText);
        Assert.Equal(TimeSpan.Zero, build.BuiltAt.Offset);
        Assert.EndsWith("+00:00", build.FullTimestampText);
    }

    [Theory]
    [InlineData(null, "2026-11-22T14:03:04Z")]
    [InlineData("development", "2026-11-22T14:03:04Z")]
    [InlineData("true", null)]
    [InlineData("true", "yesterday")]
    [InlineData("false", "2026-11-22T14:03:04")]
    public void BuildIdentityRejectsMissingOrInvalidMetadata(string? origin, string? timestamp)
    {
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.FromMetadata(origin, timestamp));
    }

    [Fact]
    public void PaletteAssignmentsAreStableAndFileKindsAreCaseInsensitive()
    {
        Assert.Equal(Palette.PairIndex("Jana Nováková"), Palette.PairIndex("  jana nováková "));
        Assert.Equal(1, Palette.PairIndex("Test School Office"));
        Assert.All(new[] { "", "a", "Marko", "Teacher" }, v => Assert.InRange(Palette.PairIndex(v), 0, Palette.PairCount - 1));
        Assert.Equal("BadgePdf", Palette.FileKey(".PDF"));
        Assert.Equal("BadgePptx", Palette.FileKey("pptx"));
        Assert.Equal("BadgeFile", Palette.FileKey(""));
        Assert.Equal("BadgeFile", Palette.FileKey("zip"));
    }

    [Fact]
    public void LargeInitialRequiresAllExplicitChildNamesToBeOneWord()
    {
        var anna = new Login("https://sample.example", "anna", ChildName: "Anna");
        var tomas = new Login("https://sample.example", "tomas", ChildName: "  Tomas  ");
        var school = new Login("https://other.example", "parent", SchoolName: "Example Primary School");
        Assert.True(AvatarPresentation.UseLargeInitial(anna, [anna, tomas, school]));
        Assert.False(AvatarPresentation.UseLargeInitial(school, [anna, tomas, school]));
        Assert.False(AvatarPresentation.UseLargeInitial(anna, [anna, tomas with { ChildName = "Tomas Novak" }]));
        Assert.False(AvatarPresentation.UseLargeInitial(anna with { ChildName = "Anna\tNovak" }, [anna]));
        Assert.False(AvatarPresentation.UseLargeInitial(null, []));
    }

    [Fact]
    public void AvatarInitialsUseWholeTextElementsAndWhitespaceSeparatedNames()
    {
        Assert.Equal("AN", AvatarPresentation.Initials(" Anna\tNovak "));
        Assert.Equal("A\u0301N", AvatarPresentation.Initials("a\u0301nna Novak"));
        Assert.Equal("A", AvatarPresentation.Initials("Anna"));
    }

    [Fact]
    public void AccountAvatarColorsDoNotDependOnNamesOrOrder()
    {
        var login = new Login("https://sample.example", "anna", ChildName: "Anna");
        var renamed = login with { ChildName = "Anna Novak" };
        var key = login.Url + "|" + login.Username;
        Assert.Equal(Palette.PairIndex(key), AvatarPresentation.PaletteIndex(key));
        Assert.Equal(AvatarPresentation.PaletteIndex(key),
            AvatarPresentation.PaletteIndex(renamed.Url + "|" + renamed.Username));
        Assert.Equal(AvatarPresentation.PaletteIndex(key),
            AvatarPresentation.PaletteIndex("  " + key.ToUpperInvariant() + "  "));
    }

    [Fact]
    public void RelatedRowsAreChronologicalNumberedOutwardAndShowTheSender()
    {
        Message Make(string id, int day, string sender) => new()
        {
            Id = id, Title = "Hodnocení", Text = "", Attachments = [],
            SentDate = new DateTimeOffset(2026, 9, day, 10, 0, 0, TimeSpan.FromHours(2)).ToString("O"),
            Sender = System.Text.Json.JsonSerializer.SerializeToElement(new { Name = sender })
        };
        var related = new RelatedMessages(
            [new(new(Make("p1", 20, "Jan Novák"), "received"), 1), new(new(Make("p2", 10, "Parent"), "sent"), 1)],
            [new(new(Make("n1", 25, "Jan Novák"), "received"), 1)]);
        var rows = RelatedRow.Build(null!, related, new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.FromHours(2)),
            new DateTime(2026, 9, 30));
        Assert.Equal(["p2", "p1", "n1"], rows.Select(r => r.Item.Message.Id));
        Assert.Equal(["Previous (2)", "Previous (1)", "Next"], rows.Select(r => r.Direction));
        Assert.Equal("you", rows[0].From);
        Assert.Equal("1 day before \u2022 From: Jan Novák", rows[1].Label);
        Assert.Equal("Hodnocení\n20 Sep 2026 10:00", rows[1].ToolTip);
        Assert.Equal("Previous (1): 1 day before \u2022 From: Jan Novák, 20 Sep 2026 10:00", rows[1].AccessibleName);
        Assert.Equal("4 days after \u2022 From: Jan Novák", rows[2].Label);
    }

    [Fact]
    public async Task ThemesAndAsyncSelectionsStayReactiveWithoutChangingArchiveReadState()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;
            var originalDefault = CultureInfo.DefaultThreadCurrentCulture;
            var originalDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _ = application.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "obk-presentation-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var store = await Task.Run(async () =>
                    {
                        var storage = new LocalStore(Path.Combine(directory, "test.sqlite3"));
                        var folders = Folders.Empty();
                        var attachments = Enumerable.Range(0, 6).Select(i => new Attachment { Id = $"file-{i}", Name = $"file-{i}.pdf" }).ToList();
                        folders["received"].Add(new Message
                        {
                            Id = "first", Title = "First", Text = "<p>First content</p><script>not rendered</script>",
                            SentDate = SchoolYear.Current().Start.ToString("O"), Read = false, Attachments = attachments
                        });
                        folders["received"].Add(new Message
                        {
                            Id = "second", Title = "Second", Text = "<p>Second content</p>",
                            SentDate = SchoolYear.Current().Start.AddDays(2).ToString("O"), Read = true, Attachments = []
                        });
                        folders["sent"].Add(new Message
                        {
                            Id = "sent-old", Title = "Older sent message", Text = "Older sent content",
                            SentDate = SchoolYear.Current().Start.ToString("O"), Read = true, Attachments = []
                        });
                        folders["sent"].Add(new Message
                        {
                            Id = "sent-new", Title = "Newest sent message", Text = "Newest sent content",
                            SentDate = SchoolYear.Current().Start.AddDays(3).ToString("O"), Read = true, Attachments = []
                        });
                        folders["sent"].Add(new Message
                        {
                            Id = "sent-excuse", Title = "omluvenka", Text = "Excuse", Read = true, Attachments = [],
                            SentDate = SchoolYear.Current().Start.AddDays(1).ToString("O"),
                            Extra = new() { ["Type"] = System.Text.Json.JsonSerializer.SerializeToElement("OMLUVENKA") }
                        });
                        storage.SaveArchive("https://school.example", "test", SchoolYear.Current(), "Test school", folders);
                        storage.SaveArchive("https://school.example", "other", SchoolYear.Current(), "Other account", folders);
                        foreach (var attachment in attachments)
                        {
                            using var input = new MemoryStream([1, 2, 3]);
                            await storage.SaveAttachmentAsync("https://school.example", "test", attachment, input, expectedSize: 3, contentType: "application/pdf");
                        }
                        return storage;
                    });
                    var theme = new ThemeManager(Path.Combine(directory, "appearance.json"));
                    string? exported = null;
                    var vm = new MainViewModel(store, theme, (_, _) => { },
                        (row, action) => { exported = row.Attachment.Identifier + ":" + action; return Task.CompletedTask; },
                        _ => Task.CompletedTask);
                    MessageRow M(string id) => vm.Messages.Single(m => m.Message.Id == id);
                    async Task WaitForFolderAsync()
                    {
                        await vm.FolderReady;
                        await vm.MessageReady;
                    }
                    await vm.InitializeAsync();
                    Assert.True(vm.ShowingAllChildren);
                    await vm.SelectAccountAsync(vm.Accounts.Single(a => a.Login.Username == "test"));
                    Assert.False(vm.ShowingAllChildren);
                    Assert.False(theme.AllChildren);
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal(2, vm.Messages.Count);
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    await vm.ActivateAccountAsync("https://school.example", "test",
                        childName: "Anežka", schoolName: "Selected school");
                    await WaitForFolderAsync();
                    Assert.Equal("Anežka", vm.AccountLabel);
                    Assert.Contains("Anežka", vm.WindowTitle);
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal("Test school", vm.AccountLabel);
                    vm.SelectedMessage = M("first");
                    await vm.MessageReady;
                    Assert.Equal(3, vm.VisibleAttachments.Count);
                    Assert.Equal("+3 more", vm.MoreAttachmentsLabel);
                    Assert.True(vm.SaveAllCommand.CanExecute(null));
                    var firstFile = vm.VisibleAttachments[0];
                    vm.OpenAttachmentCommand.Execute(firstFile);
                    Assert.Equal("file-0:open", exported);
                    vm.IsExporting = true;
                    Assert.False(vm.SaveAttachmentCommand.CanExecute(firstFile));
                    vm.IsExporting = false;
                    vm.ExpandAttachmentsCommand.Execute(null);
                    Assert.Equal(6, vm.VisibleAttachments.Count);
                    Assert.False(M("first").Message.Read);
                    Assert.False(M("first").IsNew);

                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "sent");
                    await WaitForFolderAsync();
                    Assert.Equal("sent-new", vm.SelectedMessage?.Message.Id);
                    vm.SelectedMessage = vm.Messages.Single(m => m.Message.Id == "sent-old");
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "received");
                    await WaitForFolderAsync();
                    Assert.Equal("first", vm.SelectedMessage?.Message.Id);
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "sent");
                    await WaitForFolderAsync();
                    Assert.Equal("sent-old", vm.SelectedMessage?.Message.Id);
                    Assert.DoesNotContain(vm.Messages, m => m.Message.Id == "sent-excuse");
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "excuses");
                    await WaitForFolderAsync();
                    Assert.Equal("sent-excuse", Assert.Single(vm.Messages).Message.Id);
                    Assert.Equal(["noticeboard", "received", "sent", "excuses"], vm.Navigation.Select(f => f.Key));
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "noticeboard");
                    await WaitForFolderAsync();
                    Assert.Null(vm.SelectedMessage);
                    Assert.Empty(vm.Body);
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "received");
                    await WaitForFolderAsync();
                    Assert.Equal("first", vm.SelectedMessage?.Message.Id);
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal("first", vm.SelectedMessage?.Message.Id);

                    vm.SelectedMessage = M("second");
                    vm.SelectedMessage = M("first");
                    vm.SelectedMessage = M("second");
                    await vm.MessageReady;
                    Assert.Equal("Second", vm.Subject);
                    Assert.Contains("Second content", string.Concat(vm.Body.Select(p => p.Text)));
                    Assert.False(vm.HasAttachments);
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "sent");
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "received");
                    await WaitForFolderAsync();
                    Assert.Equal("Inbox", vm.FolderTitle);
                    Assert.Equal(2, vm.Messages.Count);
                    Assert.Equal("noticeboard", vm.Navigation[0].Key);
                    Assert.Equal(["second", "first"], vm.Messages.Select(m => m.Message.Id));
                    Assert.Equal([false, true], vm.Messages.Select(m => m.IsAlternate));
                    vm.SearchText = "SECOND conténť";
                    await vm.FolderReady;
                    Assert.Equal("second", Assert.Single(vm.Messages).Message.Id);
                    Assert.False(Assert.Single(vm.Messages).IsAlternate);
                    Assert.Equal("1 of 2 messages match", vm.FolderCaption);
                    vm.SearchText = "";
                    await vm.FolderReady;
                    Assert.Equal(2, vm.Messages.Count);
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    Assert.Contains("Second content", string.Concat(vm.Body.Select(p => p.Text)));

                    vm.SelectedMessage = M("first");
                    await vm.ActivateAccountAsync("https://school.example", "other");
                    await WaitForFolderAsync();
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    vm.SelectedMessage = M("first");
                    await vm.ActivateAccountAsync("https://school.example", "other", new SchoolYear(SchoolYear.Current().StartYear + 1));
                    await WaitForFolderAsync();
                    Assert.Null(vm.SelectedMessage);
                    await vm.ActivateAccountAsync("https://school.example", "other", SchoolYear.Current());
                    await WaitForFolderAsync();
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    vm.SelectedMessage = M("first");
                    await Task.Run(() =>
                    {
                        var archive = store.LoadArchive("https://school.example", "test", SchoolYear.Current()) ??
                            throw new InvalidOperationException("Test archive is missing.");
                        archive.Folders["received"].RemoveAll(m => m.Id == "first");
                        store.SaveArchive("https://school.example", "test", SchoolYear.Current(), "Test school", archive.Folders);
                    });
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);

                    await Task.Run(() => store.SaveAccount("https://school.example", "other"));
                    await vm.ActivateAccountAsync("https://school.example", "other");
                    var accountOrder = vm.Accounts.Select(a => a.Login.Username).ToArray();
                    await Task.Run(() => store.SaveAccount("https://school.example", "test"));
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal(accountOrder, vm.Accounts.Select(a => a.Login.Username));
                    Assert.Equal("test", vm.Accounts.Single(a => a.IsCurrent).Login.Username);

                    var surface = new Border();
                    var text = new TextBlock();
                    surface.SetResourceReference(Border.BackgroundProperty, "Surface");
                    text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                    surface.Child = text;
                    var reader = new RichTextBox();
                    var panel = new StackPanel();
                    panel.Children.Add(surface);
                    panel.Children.Add(reader);
                    var window = new Window { Content = panel, Width = 240, Height = 160 };
                    window.Show();
                    MessageDocument.SetParts(reader, Enumerable.Repeat(new TextPart("stale", false, false, false, false), 200).ToArray());
                    MessageDocument.SetParts(reader, [new("current", false, false, false, false)]);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal("current\r\n", new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text);
                    Assert.Equal(Colors.White, ((SolidColorBrush)surface.Background).Color);
                    await theme.ToggleAsync();
                    Assert.Equal(Color.FromRgb(43, 43, 43), ((SolidColorBrush)surface.Background).Color);
                    Assert.Equal(Colors.White, ((SolidColorBrush)text.Foreground).Color);
                    theme.Apply("Light");
                    await theme.InitializeAsync();
                    Assert.True(theme.IsDark);
                    await theme.ToggleAsync();
                    Assert.Equal(Colors.White, ((SolidColorBrush)surface.Background).Color);
                    var settingsPath = Path.Combine(directory, "appearance.json");
                    Assert.False(theme.AlwaysRefreshAllChildren);
                    await theme.SetAlwaysRefreshAllChildrenAsync(true);
                    await theme.SetReadingSizeAsync("Large");
                    await theme.SetListDensityAsync("Compact");
                    Assert.Equal(16.0, (double)application.Resources["ReaderBodySize"]);
                    Assert.Equal(new Thickness(12, 4, 12, 4), (Thickness)application.Resources["ListRowPadding"]);
                    var restored = new ThemeManager(settingsPath);
                    var refreshSetting = new CheckBox();
                    refreshSetting.SetBinding(CheckBox.IsCheckedProperty,
                        new System.Windows.Data.Binding(nameof(ThemeManager.AlwaysRefreshAllChildren))
                        {
                            Source = restored, Mode = System.Windows.Data.BindingMode.OneWay
                        });
                    await restored.InitializeAsync();
                    Assert.True(restored.IsLargeText && restored.IsCompactList && restored.IsLight);
                    Assert.False(restored.AllChildren);
                    Assert.True(restored.AlwaysRefreshAllChildren);
                    Assert.True(refreshSetting.IsChecked);
                    await restored.SetAlwaysRefreshAllChildrenAsync(false);
                    await theme.InitializeAsync();
                    Assert.False(theme.AlwaysRefreshAllChildren);
                    await File.WriteAllTextAsync(settingsPath, "{\"Theme\":\"Light\",\"ReadingSize\":\"huge\",\"ListDensity\":\"tiny\"}");
                    await restored.InitializeAsync();
                    Assert.True(restored.IsMediumText && restored.IsRelaxedList);
                    Assert.Null(restored.AllChildren);
                    Assert.False(restored.AlwaysRefreshAllChildren);
                    Assert.False(refreshSetting.IsChecked);
                    Assert.Equal(14.0, (double)application.Resources["ReaderBodySize"]);
                    await Assert.ThrowsAsync<ObkException>(() => restored.SetReadingSizeAsync("huge"));
                    await File.WriteAllTextAsync(settingsPath, "{\"Theme\":\"invalid\"}");
                    await Assert.ThrowsAsync<ObkException>(() => theme.InitializeAsync());
                    window.Close();

                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("/OBK;component/Themes/Controls.xaml", UriKind.Relative)
                    });
                    application.Resources["VisibleIf"] = new BooleanVisibilityConverter();
                    application.Resources["AnyTrue"] = new AnyTrueConverter();
                    application.Resources["ThemeBrush"] = new ThemeBrushConverter();
                    var main = new MainWindow(store, showLogin: false, enableUpdates: false);
                    try
                    {
                        main.Show();
                        await main.InitializeAsync();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        void AssertRepositoryLink()
                        {
                            main.ViewModel.OpenSettingsCommand.Execute(null);
                            ((Expander)main.FindName("AboutSection")).IsExpanded = true;
                            main.UpdateLayout();
                            var link = (Hyperlink)main.FindName("RepositoryLink");
                            Assert.Equal("https://github.com/iricigor/OBK", link.NavigateUri.AbsoluteUri);
                            Assert.Equal(SharedStrings.RepositoryLink, new TextRange(link.ContentStart, link.ContentEnd).Text);
                            Assert.True(link.Focus());
                            Assert.True(link.IsKeyboardFocused);
                            Keyboard.ClearFocus();
                            FocusManager.SetFocusedElement(main, null);
                            ((Expander)main.FindName("AboutSection")).IsExpanded = false;
                            main.ViewModel.CloseSettingsCommand.Execute(null);
                        }
                        AssertRepositoryLink();
                        async Task AssertHelpMenuAsync()
                        {
                            main.UpdateLayout();
                            var button = (Button)main.FindName("HelpButton");
                            var status = (TextBlock)main.FindName("Status");
                            var version = (TextBlock)main.FindName("UpdateText");
                            var statusBounds = status.TransformToAncestor(main).TransformBounds(new Rect(status.RenderSize));
                            Assert.True(status.ActualWidth > 0);
                            Assert.True(button.TransformToAncestor(main).Transform(new Point()).X >
                                version.TransformToAncestor(main).Transform(new Point(version.ActualWidth, 0)).X);
                            Assert.Equal(SharedStrings.HelpMenuName, AutomationProperties.GetName(button));
                            Assert.True(button.Focus());
                            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            var menu = button.ContextMenu;
                            Assert.True(menu.IsOpen);
                            Assert.True(menu.IsKeyboardFocusWithin);
                            var actions = menu.Items.OfType<MenuItem>().ToArray();
                            Assert.Equal([HelpLinks.WindowsGuide, HelpLinks.Feedback], actions.Select(item => item.Tag));
                            Assert.Equal([SharedStrings.HelpGetHelp, SharedStrings.HelpFeedback],
                                actions.Select(AutomationProperties.GetName));
                            Assert.True(menu.PointToScreen(new Point(0, menu.ActualHeight)).Y <=
                                button.PointToScreen(new Point()).Y);
                            Assert.InRange(menu.PointToScreen(new Point(menu.ActualWidth, 0)).X -
                                button.PointToScreen(new Point(button.ActualWidth, 0)).X, -2, 2);
                            Assert.Equal(((SolidColorBrush)main.FindResource("Surface")).Color,
                                ((SolidColorBrush)menu.Background).Color);
                            var background = ((SolidColorBrush)menu.Background).Color;
                            foreach (var text in Within<TextBlock>(menu).Where(text => text.FontFamily.Source == "Segoe UI"))
                                Assert.True(Contrast(((SolidColorBrush)text.Foreground).Color, background) >= 4.5);
                            menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(menu),
                                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert.False(menu.IsOpen);
                            Assert.True(button.IsKeyboardFocused);
                            Assert.Equal(statusBounds, status.TransformToAncestor(main).TransformBounds(new Rect(status.RenderSize)));
                        }
                        await AssertHelpMenuAsync();

                        // Two children open together by default: badges, owner avatars, and opening only by user action.
                        await Task.Run(() =>
                        {
                            var archive = store.LoadArchive("https://school.example", "other", SchoolYear.Current())!;
                            foreach (var (id, day) in new[] { ("fresh", 5), ("fresher", 6) })
                                archive.Folders["received"].Add(new Message
                                {
                                    Id = id, Title = id == "fresh" ? "Fresh notice" : "Fresher notice", Text = "<p>New</p>", Attachments = [],
                                    SentDate = SchoolYear.Current().Start.AddDays(day).ToString("O")
                                });
                            store.SaveArchive("https://school.example", "other", SchoolYear.Current(), "Other account", archive.Folders);
                        });
                        // Changing the scope resets the remembered selection, so the newest message is selected again.
                        await main.ViewModel.SelectAccountAsync(main.ViewModel.Accounts.Single(account => account.Login.Username == "test"));
                        Assert.False(main.ViewModel.ShowingAllChildren);
                        await main.ViewModel.SelectAllChildrenAsync();
                        await main.ViewModel.FolderReady;
                        await main.ViewModel.MessageReady;
                        main.UpdateLayout();
                        Assert.True(main.ViewModel.ShowingAllChildren);
                        var allChildrenButton = (Button)main.FindName("AllChildrenButton");
                        Assert.Equal(Visibility.Visible, allChildrenButton.Visibility);
                        Assert.Equal("All children, new: 2", AutomationProperties.GetName(allChildrenButton));
                        Assert.Equal(2, ((ItemsControl)main.FindName("AllChildrenAvatar")).Items.Count);
                        var scopeList = (ListBox)main.FindName("Messages");
                        var folderList = (ListBox)main.FindName("FolderList");
                        ListBoxItem Item(ListBox list, object row) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                        ListBoxItem NewRow(string id) => Item(scopeList, main.ViewModel.Messages.Single(row => row.Message.Id == id));
                        var inbox = Item(folderList, main.ViewModel.Navigation.Single(folder => folder.Key == "received"));
                        Assert.Equal("Inbox, new: 2", AutomationProperties.GetName(inbox));
                        var fresher = NewRow("fresher");
                        Assert.True(fresher.IsSelected, "The newest message is selected automatically.");
                        Assert.StartsWith("New message: Fresher notice, Other account · ", AutomationProperties.GetName(fresher));
                        static IEnumerable<T> Within<T>(DependencyObject root) where T : DependencyObject
                        {
                            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                            {
                                var child = VisualTreeHelper.GetChild(root, index);
                                if (child is T match) yield return match;
                                foreach (var nested in Within<T>(child)) yield return nested;
                            }
                        }
                        var freshRow = (MessageRow)fresher.DataContext;
                        var date = Within<TextBlock>(fresher).Single(text => text.Text == freshRow.ShortSent);
                        Assert.Equal(((SolidColorBrush)main.FindResource("Accent")).Color, ((SolidColorBrush)date.Foreground).Color);
                        Assert.Equal(FontWeights.SemiBold, date.FontWeight);
                        Assert.Contains(Within<Border>(fresher), border => border.Width == 24 && border.IsVisible);
                        fresher.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        {
                            RoutedEvent = Mouse.PreviewMouseUpEvent
                        });
                        main.UpdateLayout();
                        Assert.False(freshRow.IsNew, "Clicking the automatically selected message opens it.");
                        Assert.Equal("All children, new: 1", AutomationProperties.GetName(allChildrenButton));
                        Assert.Equal("Inbox, new: 1", AutomationProperties.GetName(inbox));
                        Assert.Equal(FontWeights.Normal, date.FontWeight);
                        NewRow("fresh").IsSelected = true;
                        await main.ViewModel.MessageReady;
                        Assert.False(main.ViewModel.SelectedMessage!.IsNew, "Selecting a message in the list opens it.");
                        Assert.Equal("All children", AutomationProperties.GetName(allChildrenButton));
                        await main.ViewModel.OpenedSaved;
                        var groupBox = (ComboBox)main.FindName("GroupBox");
                        main.ViewModel.Grouping = ListGrouping.Child;
                        await main.ViewModel.FolderReady;
                        Assert.Equal(ListGrouping.Child, groupBox.SelectedValue);
                        await main.ViewModel.SelectAccountAsync(main.ViewModel.Accounts.Single(account => account.Login.Username == "test"));
                        await main.ViewModel.FolderReady;
                        await main.ViewModel.MessageReady;
                        Assert.False(main.ViewModel.ShowingAllChildren);
                        Assert.Equal(ListGrouping.None, main.ViewModel.Grouping);
                        Assert.Equal(ListGrouping.None, groupBox.SelectedValue);
                        Assert.DoesNotContain(groupBox.Items.Cast<DisplayOption<ListGrouping>>(), option => option.Value == ListGrouping.Child);
                        var ribbon = (Button)main.FindName("BuildRibbon");
                        var ribbonArea = (Canvas)main.FindName("BuildRibbonArea");
                        var readingPane = (Border)main.FindName("ReadingPane");
                        var timestamp = (TextBlock)main.FindName("BuildRibbonTimestamp");
                        Assert.Equal(BuildIdentity.Current.IsLocal ? Visibility.Visible : Visibility.Collapsed, ribbonArea.Visibility);
                        Assert.Equal(BuildIdentity.Current.TimestampText, timestamp.Text);
                        if (BuildIdentity.Current.IsLocal)
                        {
                            main.Width = 880;
                            main.Height = 620;
                            main.UpdateLayout();
                            var bounds = ribbonArea.TransformToAncestor(main).TransformBounds(new Rect(ribbonArea.RenderSize));
                            var maximize = (Button)main.FindName("MaximizeButton");
                            var captionBounds = maximize.TransformToAncestor(main).TransformBounds(new Rect(maximize.RenderSize));
                            Assert.True(bounds.Top >= captionBounds.Bottom);
                            var timestampBounds = timestamp.TransformToAncestor(ribbonArea).TransformBounds(new Rect(timestamp.RenderSize));
                            Assert.True(new Rect(ribbonArea.RenderSize).Contains(timestampBounds));
                            Assert.True(timestamp.DesiredSize.Width <= ribbon.ActualWidth - ribbon.Padding.Left - ribbon.Padding.Right);
                            var labelHit = Assert.IsAssignableFrom<DependencyObject>(main.InputHitTest(
                                timestamp.TranslatePoint(new Point(timestamp.ActualWidth / 2, timestamp.ActualHeight / 2), main)));
                            Assert.True(ribbon.IsAncestorOf(labelHit));
                            var transparentHit = Assert.IsAssignableFrom<DependencyObject>(main.InputHitTest(
                                ribbonArea.TranslatePoint(new Point(8, 120), main)));
                            Assert.False(ribbon.IsAncestorOf(transparentHit));
                            void AssertOverlayDoesNotShift(FrameworkElement content)
                            {
                                var before = content.TransformToAncestor(main).TransformBounds(new Rect(content.RenderSize));
                                ribbonArea.Visibility = Visibility.Collapsed;
                                main.UpdateLayout();
                                Assert.Equal(before, content.TransformToAncestor(main).TransformBounds(new Rect(content.RenderSize)));
                                ribbonArea.Visibility = Visibility.Visible;
                                main.UpdateLayout();
                            }
                            AssertOverlayDoesNotShift(readingPane);
                            main.ViewModel.OpenSettingsCommand.Execute(null);
                            main.UpdateLayout();
                            AssertOverlayDoesNotShift((ScrollViewer)main.FindName("SettingsPage"));
                            main.ViewModel.CloseSettingsCommand.Execute(null);
                            main.UpdateLayout();
                            Assert.True(ribbon.Focus());
                            Assert.True(ribbon.IsKeyboardFocused);
                        }
                        var handle = new WindowInteropHelper(main).Handle;
                        void AssertNativeRounding()
                        {
                            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
                            Assert.Equal(0, DwmGetWindowAttribute(handle, 33, out var preference, sizeof(int)));
                            Assert.Equal(2, preference);
                            var region = CreateRectRgn(0, 0, 0, 0);
                            Assert.NotEqual(IntPtr.Zero, region);
                            try
                            {
                                // Even a rectangular WPF window region blocks compositor rounding.
                                Assert.Equal(0, GetWindowRgn(handle, region));
                            }
                            finally { Assert.True(DeleteObject(region)); }
                        }
                        AssertNativeRounding();
                        main.Width = 1000;
                        main.Height = 700;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        AssertNativeRounding();
                        if (BuildIdentity.Current.IsLocal)
                        {
                            var beforeDismissal = readingPane.TransformToAncestor(main).TransformBounds(new Rect(readingPane.RenderSize));
                            var invoke = (IInvokeProvider)new ButtonAutomationPeer(ribbon).GetPattern(PatternInterface.Invoke);
                            invoke.Invoke();
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert.Equal(Visibility.Collapsed, ribbonArea.Visibility);
                            Assert.Equal(beforeDismissal, readingPane.TransformToAncestor(main).TransformBounds(new Rect(readingPane.RenderSize)));
                            var reopened = new MainWindow(store, showLogin: false, enableUpdates: false);
                            try
                            {
                                reopened.Show();
                                Assert.Equal(Visibility.Collapsed, ((Canvas)reopened.FindName("BuildRibbonArea")).Visibility);
                            }
                            finally { reopened.Close(); }
                        }
                        main.MaximizeCommand.Execute(null);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.Equal(WindowState.Maximized, main.WindowState);
                        main.MaximizeCommand.Execute(null);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.Equal(WindowState.Normal, main.WindowState);
                        AssertNativeRounding();
                        await main.ViewModel.Theme.ToggleAsync();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        AssertNativeRounding();

                        var readerFolders = Folders.Empty();
                        Message ReaderMessage(string id, string title, int day) => new()
                        {
                            Id = id, Title = title, Text = "<p>Reader surface fixture</p>",
                            SentDate = SchoolYear.Current().Start.AddDays(day).ToString("O"),
                            Sender = System.Text.Json.JsonSerializer.SerializeToElement(new { Name = "Test Teacher" }),
                            Read = true, Attachments = []
                        };
                        readerFolders["received"].Add(ReaderMessage("original", "Reader shades", 1));
                        readerFolders["received"][0].Attachments.Add(new Attachment { Id = "reader-file", Name = "schedule.pdf" });
                        readerFolders["received"].Add(ReaderMessage("unrelated", "Separate notice", 3));
                        readerFolders["sent"].Add(ReaderMessage("reply", "Re: Reader shades", 2));
                        await Task.Run(() => store.SaveArchive("https://school.example", "reader-shades",
                            SchoolYear.Current(), "Reader test school", readerFolders));
                        await main.ViewModel.ActivateAccountAsync("https://school.example", "reader-shades");
                        await main.ViewModel.FolderReady;
                        main.ViewModel.SelectedMessage = main.ViewModel.Messages.Single(row => row.Message.Id == "original");
                        await main.ViewModel.MessageReady;
                        var header = (Border)main.FindName("MessageHeader");
                        var band = (Border)main.FindName("RelatedBand");
                        var attachmentBand = (Border)main.FindName("AttachmentBand");
                        var attachmentPanel = (StackPanel)main.FindName("AttachmentPanel");
                        var contentReader = (RichTextBox)main.FindName("ContentReader");
                        Assert.Same(attachmentBand, attachmentPanel.Parent);
                        Assert.False(attachmentBand.IsAncestorOf(contentReader));
                        var relatedPanel = (StackPanel)main.FindName("RelatedPanel");
                        var relatedItems = Assert.IsType<ItemsControl>(relatedPanel.Children[1]);
                        var messageList = (ListBox)main.FindName("Messages");
                        static IEnumerable<ListBoxItem> RowContainers(DependencyObject root)
                        {
                            if (root is ListBoxItem row) yield return row;
                            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                                foreach (var child in RowContainers(VisualTreeHelper.GetChild(root, index)))
                                    yield return child;
                        }
                        Assert.True(main.ViewModel.HasRelated);
                        foreach (var mode in new[] { "Light", "Dark" })
                        {
                            main.ViewModel.Theme.Apply(mode);
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            await AssertHelpMenuAsync();
                            main.UpdateLayout();
                            var rows = RowContainers(messageList).ToArray();
                            Assert.Equal(2, rows.Length);
                            var striped = Assert.Single(rows, row => ((MessageRow)row.DataContext).IsAlternate);
                            var stripe = ((SolidColorBrush)main.FindResource("ListAlternateSurface")).Color;
                            Assert.Equal(stripe, ((SolidColorBrush)striped.Background).Color);
                            var selected = Assert.Single(rows, row => row.IsSelected);
                            var selectedChrome = (Border)selected.Template.FindName("Chrome", selected);
                            Assert.Equal(((SolidColorBrush)main.FindResource("Selection")).Color,
                                ((SolidColorBrush)selectedChrome.Background).Color);
                            Assert.True(Contrast(((SolidColorBrush)main.FindResource("TextSecondary")).Color, stripe) >= 4.5);
                            Assert.Equal(Visibility.Visible, band.Visibility);
                            Assert.Equal(Visibility.Visible, attachmentBand.Visibility);
                            Assert.Equal(band.ActualWidth, attachmentBand.ActualWidth);
                            var attachmentSurface = ((SolidColorBrush)attachmentBand.Background).Color;
                            Assert.NotEqual(((SolidColorBrush)readingPane.Background).Color, attachmentSurface);
                            Assert.NotEqual(((SolidColorBrush)header.Background).Color, ((SolidColorBrush)band.Background).Color);
                            Assert.Equal(header.ActualWidth, band.ActualWidth);
                            Assert.True(header.ActualWidth > 0);
                            Assert.Equal(new CornerRadius(7, 7, 0, 0), header.CornerRadius);
                            var secondary = (SolidColorBrush)main.FindResource("MessageHeaderSecondary");
                            var primary = (SolidColorBrush)main.FindResource("TextPrimary");
                            var accent = (SolidColorBrush)main.FindResource("Accent");
                            var relatedLink = (SolidColorBrush)main.FindResource("RelatedLink");
                            Assert.True(Contrast(primary.Color, attachmentSurface) >= 4.5);
                            Assert.True(Contrast(accent.Color, attachmentSurface) >= 4.5);
                            Assert.True(Contrast(primary.Color, ((SolidColorBrush)header.Background).Color) >= 4.5);
                            Assert.True(Contrast(secondary.Color, ((SolidColorBrush)header.Background).Color) >= 4.5);
                            Assert.True(Contrast(secondary.Color, ((SolidColorBrush)band.Background).Color) >= 4.5);
                            foreach (var surfaceKey in new[] { "MessageHeaderSurface", "Hover", "Selection" })
                                Assert.True(Contrast(relatedLink.Color, ((SolidColorBrush)main.FindResource(surfaceKey)).Color) >= 4.5,
                                    $"{mode} related links must remain readable on {surfaceKey}.");
                            var presenter = Assert.IsType<ContentPresenter>(relatedItems.ItemContainerGenerator.ContainerFromIndex(0));
                            var relatedButton = Assert.IsType<Button>(VisualTreeHelper.GetChild(presenter, 0));
                            Assert.True(relatedButton.Focus());
                            Assert.True(relatedButton.IsKeyboardFocused);
                            var chrome = Assert.IsType<Border>(VisualTreeHelper.GetChild(relatedButton, 0));
                            Assert.Equal(accent.Color, ((SolidColorBrush)chrome.BorderBrush).Color);
                            Assert.True(Contrast(accent.Color, ((SolidColorBrush)band.Background).Color) >= 3);
                        }
                        main.Width = 880;
                        main.Height = 620;
                        await main.ViewModel.Theme.SetReadingSizeAsync("Large");
                        main.UpdateLayout();
                        await AssertHelpMenuAsync();
                        Assert.Equal(header.ActualWidth, band.ActualWidth);
                        Assert.True(band.ActualHeight > 0);
                        main.ViewModel.OpenRelatedCommand.Execute(Assert.Single(main.ViewModel.Related));
                        await main.ViewModel.FolderReady;
                        await main.ViewModel.MessageReady;
                        Assert.Equal("sent", main.ViewModel.SelectedFolder!.Key);
                        Assert.Equal("reply", main.ViewModel.SelectedMessage!.Message.Id);
                        main.ViewModel.SelectedFolder = main.ViewModel.Navigation.Single(folder => folder.Key == "received");
                        await main.ViewModel.FolderReady;
                        main.ViewModel.SelectedMessage = main.ViewModel.Messages.Single(row => row.Message.Id == "unrelated");
                        await main.ViewModel.MessageReady;
                        main.UpdateLayout();
                        Assert.False(main.ViewModel.HasRelated);
                        Assert.Equal(Visibility.Collapsed, band.Visibility);
                        Assert.Equal(0, band.ActualHeight);
                        Assert.Equal(Visibility.Collapsed, attachmentBand.Visibility);
                        Assert.Equal(0, attachmentBand.ActualHeight);
                        await main.ViewModel.Theme.SetReadingSizeAsync("Medium");
                        main.Width = 1000;
                        main.Height = 700;

                        await main.ViewModel.ActivateAccountAsync("https://school.example", "test");
                        main.ViewModel.SelectedFolder = main.ViewModel.Navigation.Single(folder => folder.Key == "sent");
                        await main.ViewModel.FolderReady;
                        main.ViewModel.SearchText = "sent Sep";
                        main.ViewModel.Sort = MessageSort.Sender;
                        main.ViewModel.Grouping = ListGrouping.Sender;
                        await main.ViewModel.FolderReady;
                        main.ViewModel.SelectedMessage = main.ViewModel.Messages.Single(row => row.Message.Id == "sent-old");
                        await main.ViewModel.MessageReady;
                        var updates = main.Updates;
                        var appearance = main.ViewModel.Theme;
                        var wasDark = appearance.IsDark;
                        async Task<MainWindow> ClickLanguageAsync(MainWindow current)
                        {
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            var button = (Button)current.FindName("LanguageButton");
                            Assert.True(button.IsEnabled, "The language button must be enabled after loading.");
                            var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
                            invoke.Invoke();
                            for (var attempt = 0; attempt < 500; attempt++)
                            {
                                if (application.MainWindow is MainWindow next && next != current)
                                {
                                    await next.InitializeAsync();
                                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                    Assert.True(((Button)next.FindName("LanguageButton")).IsEnabled,
                                        "The rebuilt window must allow the next language switch.");
                                    Assert.Equal(0, DwmGetWindowAttribute(new WindowInteropHelper(next).Handle, 14, out var cloaked, sizeof(int)));
                                    Assert.True(cloaked == 0, "The rebuilt window must be revealed after rendering.");
                                    Assert.False(current.IsVisible, "The previous window must close only after the replacement is revealed.");
                                    return next;
                                }
                                await Task.Delay(10);
                            }
                            throw new TimeoutException("The language button did not rebuild the window.");
                        }
                        LanguageSettings.Apply("en");
                        main.ViewModel.OpenSettingsCommand.Execute(null);
                        ((Expander)main.FindName("RefreshSection")).IsExpanded = true;
                        await appearance.SetAlwaysRefreshAllChildrenAsync(true);
                        foreach (var name in new[] { "Messages", "FolderList" })
                        {
                            var list = (ListBox)main.FindName(name);
                            list.IsEnabled = false;
                            list.UpdateLayout();
                            var chrome = (Border)VisualTreeHelper.GetChild(list, 0);
                            Assert.True(chrome.Background is SolidColorBrush { Color.A: 0 },
                                $"{name} must keep its themed surface while disabled during a language switch.");
                            list.IsEnabled = true;
                        }
                        main = await ClickLanguageAsync(main);
                        Assert.Equal("cs", LanguageSettings.Code);
                        Assert.Equal("CS", ((TextBlock)main.FindName("LanguageCodeText")).Text);
                        Assert.True(main.ViewModel.SettingsOpen);
                        Assert.True(((Expander)main.FindName("RefreshSection")).IsExpanded);
                        Assert.True(main.ViewModel.Theme.AlwaysRefreshAllChildren);
                        AssertRepositoryLink();
                        Assert.Equal("Odeslané", main.ViewModel.FolderTitle);
                        await AssertHelpMenuAsync();
                        Assert.Equal("test", main.Account!.Username);
                        Assert.Equal("sent Sep", main.ViewModel.SearchText);
                        Assert.Equal(MessageSort.Sender, main.ViewModel.Sort);
                        Assert.Equal(ListGrouping.Sender, main.ViewModel.Grouping);
                        Assert.Equal("sent-old", main.ViewModel.SelectedMessage!.Message.Id);
                        Assert.Same(updates, main.Updates);
                        Assert.Same(appearance, main.ViewModel.Theme);
                        Assert.Equal(wasDark, appearance.IsDark);
                        Assert.Equal("cs", await LanguageSettings.LoadAsync(Path.Combine(directory, "language.json"), "en-US"));
                        Assert.Equal("20. 11. 2026 14:09",
                            new DateTime(2026, 11, 20, 14, 9, 0).ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture));
                        Assert.Equal("1,5 MB", AttachmentRow.FormatSize(1572864));
                        Assert.Equal("listopad 2025", MessageInsights.DateGroup(new DateTimeOffset(2025, 11, 1, 12, 0, 0, TimeSpan.Zero), new DateTime(2026, 11, 22)));
                        main = await ClickLanguageAsync(main);
                        Assert.Equal("uk", LanguageSettings.Code);
                        Assert.Equal("UK", ((TextBlock)main.FindName("LanguageCodeText")).Text);
                        AssertRepositoryLink();
                        await AssertHelpMenuAsync();
                        Assert.Equal("sent-old", main.ViewModel.SelectedMessage!.Message.Id);
                        Assert.Equal("20 листопада 2026 р. 14:09",
                            new DateTime(2026, 11, 20, 14, 9, 0).ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture));
                        Assert.Equal("1,5 МБ", AttachmentRow.FormatSize(1572864));
                        main = await ClickLanguageAsync(main);
                        Assert.Equal("de", LanguageSettings.Code);
                        Assert.Equal("DE", ((TextBlock)main.FindName("LanguageCodeText")).Text);
                        AssertRepositoryLink();
                        await AssertHelpMenuAsync();
                        Assert.Equal("sent-old", main.ViewModel.SelectedMessage!.Message.Id);
                        Assert.Equal("20. November 2026 14:09",
                            new DateTime(2026, 11, 20, 14, 9, 0).ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture));
                        Assert.Equal("1,5 MB", AttachmentRow.FormatSize(1572864));
                        main = await ClickLanguageAsync(main);
                        Assert.Equal("en", LanguageSettings.Code);
                        AssertRepositoryLink();
                        Assert.Equal("Sent", main.ViewModel.FolderTitle);
                        Assert.Equal("sent-old", main.ViewModel.SelectedMessage!.Message.Id);
                        Assert.Equal("1.5 MB", AttachmentRow.FormatSize(1572864));
                    }
                    finally { main.Close(); }
                    completion.SetResult();
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    completion.SetException(error);
                }
                finally
                {
                    SharedStrings.Culture = null;
                    OBK.App.Localization.AppStrings.Culture = null;
                    CultureInfo.CurrentCulture = originalCulture;
                    CultureInfo.CurrentUICulture = originalUiCulture;
                    CultureInfo.DefaultThreadCurrentCulture = originalDefault;
                    CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUi;
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    application.Shutdown();
                }
            }));
            application.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        thread.Join();
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value)
            {
                var channel = value / 255.0;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
