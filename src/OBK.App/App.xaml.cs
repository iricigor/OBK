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
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += UnexpectedError;
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
            try { new MainWindow(new LocalStore()).Show(); }
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
            var store = new LocalStore(Path.Combine(directory, "smoke.sqlite3"));
            if (LocalStore.Decode<string>(LocalStore.Encode("installation-check")) != "installation-check")
                throw new ObkException("Windows protection round-trip failed.");
            var year = SchoolYear.Current();
            var folders = Folders.Empty();
            folders["received"].Add(new Message
            {
                Id = "installation-check", Title = "Installation check",
                Text = "<p>Offline content with <strong>formatting</strong>.</p><script>never execute</script>",
                SentDate = year.Start.ToString("O"), Read = false,
                Attachments = [new Attachment { Id = "not-downloaded", Name = "agenda.pdf" }]
            });
            store.SaveLogin("https://school.example", "installation-check", "", false);
            store.SaveArchive("https://school.example", "installation-check", year, "Installation check school", folders);
            var window = new MainWindow(store, showLogin: false);
            MainWindow = window;
            window.Show();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var exitCode = 0;
                try
                {
                    if (!window.IsVisible || window.ActualWidth <= 0) throw new ObkException("The offline window did not start.");
                    window.VerifyOfflineReader();
                    window.UpdateLayout();
                    if (resultPath is not null)
                    {
                        Capture(window, Path.ChangeExtension(resultPath, ".png"));
                        window.Width = 880;
                        window.Height = 620;
                        window.UpdateLayout();
                        Capture(window, Path.ChangeExtension(resultPath, ".small.png"));
                        var login = new LoginWindow(window, window.Account, false, "");
                        login.Show();
                        login.UpdateLayout();
                        Capture(login, Path.ChangeExtension(resultPath, ".login.png"));
                        login.Complete();
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
