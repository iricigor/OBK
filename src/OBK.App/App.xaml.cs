using OBK.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OBK.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += UnexpectedError;
        if (e.Args.Length == 2 && e.Args[0] == "--screenshot")
        {
            await CaptureDemoAsync(e.Args[1]);
            return;
        }
        var smoke = e.Args.Contains("--smoke-test");
        var resultIndex = Array.IndexOf(e.Args, "--smoke-result");
        string? resultPath = resultIndex >= 0 && resultIndex + 1 < e.Args.Length ? e.Args[resultIndex + 1] : null;
        if (e.Args.Length != (smoke ? 1 : 0) + (resultPath is not null ? 2 : 0) || (resultIndex >= 0 && !smoke))
        {
            MessageBox.Show("Usage: OBK.exe [--smoke-test [--smoke-result path]]", "Invalid arguments");
            Shutdown(1);
            return;
        }
        if (!smoke)
        {
            try { new MainWindow(await Task.Run(() => new LocalStore())).Show(); }
            catch (ObkException error)
            {
                MessageBox.Show(error.Message, "OBK cannot start", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "obk-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = await Task.Run(() => new LocalStore(Path.Combine(directory, "smoke.sqlite3")));
            if (LocalStore.Decode<string>(LocalStore.Encode("installation-check")) != "installation-check")
                throw new ObkException("Windows protection round-trip failed.");
            var year = SchoolYear.Current();
            var folders = Folders.Empty();
            folders["received"].Add(new Message
            {
                Id = "installation-check", Title = "Installation check",
                Text = "<p>Offline content with <strong>formatting</strong>.</p><script>never execute</script>",
                SentDate = year.Start.ToString("O"), Read = false,
                Sender = JsonSerializer.SerializeToElement(new { Name = "Test School Office" }),
                Attachments = Enumerable.Range(1, 5).Select(i => new Attachment { Id = $"fixture-{i}", Name = $"offline-agenda-{i}.pdf" })
                    .Append(new Attachment { Id = "not-downloaded", Name = "not-downloaded.pdf" }).ToList()
            });
            folders["received"].Add(new Message
            {
                Id = "fixture-second", Title = "Offline reading, folders and attachments",
                Text = string.Concat(Enumerable.Range(1, 60).Select(i => $"<p>Synthetic offline content, paragraph {i}. No school is contacted.</p>")),
                SentDate = year.Start.AddDays(1).ToString("O"), Read = true,
                Sender = JsonSerializer.SerializeToElement(new { Name = "Test School Office" }), Attachments = []
            });
            store.SaveLogin("https://school.example", "installation-check", "", true,
                "Installation check pupil", "Installation check school");
            store.SaveLogin("https://school-previous.example", "previous-account", "unused", false,
                "Previous pupil", "Previous school");
            store.SaveLogin("https://school.example", "installation-check", "", true,
                "Installation check pupil", "Installation check school");
            store.SaveArchive("https://school.example", "installation-check", year, "Installation check school", folders);
            await Task.Run(async () =>
            {
                foreach (var attachment in folders["received"][0].Attachments.Take(5))
                {
                    using var source = new MemoryStream(new byte[459712]);
                    await store.SaveAttachmentAsync("https://school.example", "installation-check", attachment, source, expectedSize: 459712, contentType: "application/pdf");
                }
            });
            var window = new MainWindow(store, showLogin: false, enableUpdates: false);
            MainWindow = window;
            window.Show();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                var exitCode = 0;
                try
                {
                    if (!window.IsVisible || window.ActualWidth <= 0) throw new ObkException("The offline window did not start.");
                    await window.InitializeAsync();
                    if (window.ViewModel.AccountLabel != "Installation check pupil")
                        throw new ObkException("The optional child name was not restored for the active account.");
                    await window.VerifyOfflineReaderAsync();
                    window.UpdateLayout();
                    if (resultPath is not null)
                    {
                        Capture(window, Path.ChangeExtension(resultPath, ".png"));
                        await window.ViewModel.Theme.ToggleAsync();
                        if (((SolidColorBrush)window.Background).Color != Color.FromRgb(32, 32, 32) ||
                            ((SolidColorBrush)window.ContentReader.Foreground).Color != Colors.White)
                            throw new ObkException("Theme resources did not update the live window and message text.");
                        await window.ViewModel.Theme.InitializeAsync();
                        if (!window.ViewModel.Theme.IsDark) throw new ObkException("The selected theme was not persisted.");
                        window.UpdateLayout();
                        Capture(window, Path.ChangeExtension(resultPath, ".dark.png"));
                        window.Width = 880;
                        window.Height = 620;
                        window.UpdateLayout();
                        Capture(window, Path.ChangeExtension(resultPath, ".small.png"));
                        window.ViewModel.ToggleNavigationCommand.Execute(null);
                        window.UpdateLayout();
                        if (Math.Abs(window.NavigationColumn.ActualWidth - 64) > 0.5)
                            throw new ObkException("Collapsing the sidebar did not switch to icon-only width.");
                        Capture(window, Path.ChangeExtension(resultPath, ".icons.png"));
                        window.ViewModel.ToggleNavigationCommand.Execute(null);
                        window.UpdateLayout();
                        if (window.NavigationColumn.ActualWidth < 140)
                            throw new ObkException("Expanding the sidebar did not restore its width.");
                        window.ViewModel.Grouping = MessageGrouping.Date;
                        await window.ViewModel.FolderReady;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        window.UpdateLayout();
                        var groupHeader = Descendants(window.Messages).OfType<System.Windows.Controls.Primitives.ToggleButton>()
                            .FirstOrDefault(t => t.Name == "GroupHeader") ?? throw new ObkException("Grouped messages have no group header.");
                        groupHeader.IsChecked = false;
                        window.UpdateLayout();
                        if (Descendants(window.Messages).OfType<System.Windows.Controls.ListBoxItem>().Any(i => i.IsVisible))
                            throw new ObkException("Collapsing a message group did not hide its messages.");
                        Capture(window, Path.ChangeExtension(resultPath, ".grouped.png"));
                        groupHeader.IsChecked = true;
                        window.ViewModel.Grouping = MessageGrouping.None;
                        await window.ViewModel.FolderReady;
                        string? switchUsername = null;
                        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                        {
                            var picker = window.OwnedWindows.OfType<LoginWindow>().Single();
                            switchUsername = picker.Username.Text;
                            picker.Close();
                        }));
                        window.ViewModel.SwitchSchoolCommand.Execute(null);
                        if (switchUsername != "" || window.Account?.Username != "installation-check")
                            throw new ObkException("Cancelling Switch school did not return to the unchanged current account.");
                        var login = new LoginWindow(window, null, false, "", showSavedAccounts: true);
                        login.Show();
                        await login.DirectoryReady;
                        if (login.AvailableSchoolCount < 1000)
                            throw new ObkException("The bundled school directory was not loaded by the sign-in window.");
                        if (login.RememberedProfileCount != 2 || login.SavedAccountsPanel.Visibility != Visibility.Visible)
                            throw new ObkException("Previously used accounts were not offered during initial sign-in.");
                        login.UpdateLayout();
                        Capture(login, Path.ChangeExtension(resultPath, ".login-accounts.png"));
                        login.SchoolSearch.Text = "bit";
                        await Dispatcher.Yield(DispatcherPriority.Input);
                        login.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        if (login.SchoolMatches.Items.Count != 6 || login.SavedAccountsPanel.Visibility == Visibility.Visible)
                            throw new ObkException("Searching did not show six compact matches in place of saved accounts.");
                        if (SystemParameters.WorkArea.Height >= 760 &&
                            login.ContentScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                            throw new ObkException("Six school matches required a scrollbar in the sign-in window.");
                        Capture(login, Path.ChangeExtension(resultPath, ".login-six.png"));
                        login.SchoolSearch.Text = "zssteken";
                        await Dispatcher.Yield(DispatcherPriority.Input);
                        if (login.SchoolMatches.Visibility != Visibility.Visible ||
                            login.SchoolMatches.Items.Count != 1)
                            throw new ObkException("School search did not narrow the visible list to the matching school.");
                        login.UpdateLayout();
                        Capture(login, Path.ChangeExtension(resultPath, ".login-search.png"));
                        login.SchoolMatches.SelectedIndex = 0;
                        login.CommitSchoolSelection();
                        if (login.SelectedSchoolPanel.Visibility != Visibility.Visible ||
                            login.CredentialsPanel.Visibility != Visibility.Visible ||
                            !login.SchoolUrl.Text.Contains("zssteken", StringComparison.OrdinalIgnoreCase))
                            throw new ObkException("Selecting a school search result did not populate its URL.");
                        login.Username.Text = "school-account";
                        login.Password.Password = "secret";
                        login.ChildName.Text = "Child";
                        login.Remember.IsChecked = true;
                        login.UpdateLayout();
                        Capture(login, Path.ChangeExtension(resultPath, ".login.png"));
                        login.ChangeSchoolButton.RaiseEvent(
                            new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Dispatcher.Yield(DispatcherPriority.Input);
                        if (login.Username.Text.Length != 0 || login.Password.Password.Length != 0 ||
                            login.ChildName.Text.Length != 0 || login.Remember.IsChecked == true)
                            throw new ObkException("Changing schools did not clear the previous account fields.");
                        login.UseRememberedProfile(1);
                        if (login.Username.Text != "installation-check" ||
                            login.ChildName.Text != "Installation check pupil" || login.Remember.IsChecked != true)
                            throw new ObkException("Selecting a remembered account did not restore its profile.");
                        login.Remember.IsChecked = false;
                        for (var wait = 0; wait < 100 && !login.Remember.IsEnabled; wait++) await Task.Delay(50);
                        if (store.RememberedLogins().Any(profile => profile.Username == "installation-check"))
                            throw new ObkException("Unchecking Remember did not forget the saved password.");
                        login.Close();
                        if (store.LastAccount()!.Username != "installation-check")
                            throw new ObkException("Closing the sign-in window changed the active offline account.");
                    }
                    if (resultPath is not null) File.WriteAllText(resultPath, "{\"ok\":true}");
                }
                catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
                {
                    exitCode = 1;
                    if (resultPath is not null) File.WriteAllText(resultPath, JsonSerializer.Serialize(new { ok = false, error = error.Message }));
                }
                finally
                {
                    window.Close();
                    Directory.Delete(directory, recursive: true);
                    Shutdown(exitCode);
                }
            }));
        }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            if (resultPath is not null) File.WriteAllText(resultPath, JsonSerializer.Serialize(new { ok = false, error = error.Message }));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Shutdown(1);
        }
    }

    /// <summary>Renders fictional sample data for documentation screenshots; no school is contacted.</summary>
    private async Task CaptureDemoAsync(string path)
    {
        var directory = Path.Combine(Path.GetTempPath(), "obk-demo-" + Guid.NewGuid().ToString("N"));
        var exitCode = 0;
        try
        {
            var store = await Task.Run(() => new LocalStore(Path.Combine(directory, "demo.sqlite3")));
            var year = SchoolYear.Current();
            var now = DateTimeOffset.Now;
            Message Make(string id, string title, string sender, TimeSpan age, string text, bool read = true,
                string? recipient = null, string? type = null, int files = 0) => new()
            {
                Id = id, Title = title, Text = text, Read = read, RelevantName = recipient,
                SentDate = (now - age < year.Start ? year.Start.AddDays(1) : now - age).ToString("O"),
                Sender = JsonSerializer.SerializeToElement(new { Name = sender }),
                Attachments = Enumerable.Range(1, files).Select(i => new Attachment { Id = $"{id}-{i}", Name = $"{id}-{i}.pdf" }).ToList(),
                Extra = type is null ? null : new() { ["Type"] = JsonSerializer.SerializeToElement(type) }
            };
            var folders = Folders.Empty();
            folders["received"].AddRange(
            [
                Make("trip", "Školní výlet do Brna", "Mgr. Eva Svobodová", TimeSpan.FromMinutes(40),
                    "<p>Dobrý den,</p><p>ve čtvrtek pojedeme na <b>školní výlet do Brna</b>. Sraz je v 7:30 před školou.</p>" +
                    "<p>Program a mapu najdete na https://www.example.com/vylet.</p><p>S pozdravem<br>Eva Svobodová</p>",
                    read: false, files: 2),
                Make("grades", "Hodnocení / Evaluation - Matematika", "Ing. Petr Novák", TimeSpan.FromDays(2),
                    "<p>Posílám průběžné hodnocení z matematiky za první čtvrtletí.</p>"),
                Make("lunch", "Změna jídelníčku", "Školní jídelna", TimeSpan.FromDays(4),
                    "<p>Od pondělí se mění jídelníček.</p>", read: false),
                Make("meeting", "Třídní schůzky", "Mgr. Eva Svobodová", TimeSpan.FromDays(9),
                    "<p>Třídní schůzky proběhnou v úterý od 17:00.</p>", files: 1),
                Make("library", "Školní knihovna", "Knihovna", TimeSpan.FromDays(16), "<p>Knihovna je otevřena i o přestávkách.</p>")
            ]);
            folders["sent"].AddRange(
            [
                Make("grades-reply", "Re: Hodnocení - Matematika", "Parent", TimeSpan.FromDays(1),
                    "<p>Děkuji za hodnocení.</p>", recipient: "Ing. Petr Novák"),
                Make("excuse", "omluvenka: Anna Dvořáková, 5.A", "Parent", TimeSpan.FromDays(6),
                    "<p>Omlouvám dceru z důvodu nemoci.</p>", recipient: "Mgr. Eva Svobodová", type: "OMLUVENKA")
            ]);
            folders["noticeboard"].Add(Make("holiday", "Podzimní prázdniny", "Ředitelství", TimeSpan.FromDays(12),
                "<p>Podzimní prázdniny budou 29. a 30. října.</p>"));
            await Task.Run(() =>
            {
                store.SaveLogin("https://tomas.example", "tomas", "unused", false, "Tomáš", "Základní škola Lipová");
                store.SaveLogin("https://anna.example", "anna", "unused", false, "Anna", "Základní škola Lipová");
                store.SaveArchive("https://anna.example", "anna", year, "Základní škola Lipová", folders);
                store.SaveArchive("https://tomas.example", "tomas", year, "Základní škola Lipová", Folders.Empty());
            });
            foreach (var attachment in folders["received"].SelectMany(m => m.Attachments))
            {
                using var source = new MemoryStream(new byte[184320]);
                await store.SaveAttachmentAsync("https://anna.example", "anna", attachment, source, expectedSize: 184320, contentType: "application/pdf");
            }
            var window = new MainWindow(store, showLogin: false, enableUpdates: false) { Width = 1280, Height = 760 };
            MainWindow = window;
            window.Show();
            await window.InitializeAsync();
            window.ViewModel.Grouping = MessageGrouping.Date;
            await window.ViewModel.FolderReady;
            window.ViewModel.SelectedMessage = window.ViewModel.Messages.Single(m => m.Message.Id == "grades");
            await window.ViewModel.MessageReady;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Capture(window, path);
            window.Close();
        }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            exitCode = 1;
            MessageBox.Show(error.Message, "Screenshot failed");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Shutdown(exitCode);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private void UnexpectedError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Unexpected application error ({e.Exception.GetType().Name}). Restart OBK; do not share credentials or message content in error reports.",
            "OBK error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }
}
