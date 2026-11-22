using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using QRCoder;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OBK.App;

/// <summary>Shows one account's encrypted QR code and unlock words briefly, then hides both.</summary>
public partial class TransferWindow : Window
{
    public static readonly TimeSpan VisibleFor = TimeSpan.FromSeconds(15);
    private readonly Func<Task<Login>> loadLogin;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime hideAt;
    private bool generating;

    public TransferWindow(Window owner, string label, Func<Task<Login>> loadLogin)
    {
        InitializeComponent();
        Owner = owner;
        AccountText.Text = label;
        this.loadLogin = loadLogin;
        timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) => await ShowNewAsync();
        Closed += (_, _) => ClearCode(null);
    }

    internal bool CodeVisible => CodePanel.Visibility == Visibility.Visible;
    internal string Words => WordsText.Text;

    private async void ShowNewClick(object sender, RoutedEventArgs e) => await ShowNewAsync();

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    internal async Task ShowNewAsync()
    {
        if (generating) return;
        generating = true;
        ShowNewButton.IsEnabled = false;
        ClearCode(AppStrings.TransferPreparing);
        try
        {
            var login = await loadLogin();
            var (transfer, image) = await Task.Run(() =>
            {
                var created = CredentialTransfer.Create(login);
                return (created, RenderQr(created.Code));
            });
            if (!IsLoaded) return;
            QrImage.Source = image;
            WordsText.Text = transfer.Passphrase;
            CodePanel.Visibility = Visibility.Visible;
            PlaceholderText.Visibility = Visibility.Collapsed;
            hideAt = DateTime.UtcNow + VisibleFor;
            Tick();
            timer.Start();
        }
        catch (ObkException error) { ClearCode(error.Message); }
        finally
        {
            generating = false;
            ShowNewButton.IsEnabled = true;
        }
    }

    private void Tick()
    {
        var left = (int)Math.Ceiling((hideAt - DateTime.UtcNow).TotalSeconds);
        if (left <= 0) ClearCode(AppStrings.TransferHidden);
        else CountdownText.Text = Localized.Format(AppStrings.TransferCountdown, left);
    }

    private void ClearCode(string? message)
    {
        timer.Stop();
        QrImage.Source = null;
        WordsText.Text = "";
        CountdownText.Text = "";
        CodePanel.Visibility = Visibility.Collapsed;
        PlaceholderText.Text = message ?? "";
        PlaceholderText.Visibility = Visibility.Visible;
    }

    private static DrawingImage RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var y = 0; y < modules.Count; y++)
                for (var x = 0; x < modules[y].Count; x++)
                    if (modules[y][x])
                    {
                        context.BeginFigure(new Point(x, y), true, true);
                        context.PolyLineTo([new Point(x + 1, y), new Point(x + 1, y + 1), new Point(x, y + 1)], false, false);
                    }
        }
        geometry.Freeze();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, modules.Count, modules.Count));
            context.DrawGeometry(Brushes.Black, null, geometry);
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
