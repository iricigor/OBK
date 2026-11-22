using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace OBK.AndroidApp;

/// <summary>Scans the encrypted account QR code shown by OBK on Windows.</summary>
internal sealed class TransferScanPage : ContentPage
{
    private readonly Action<Login> imported;
    private readonly Label status = new() { Text = AppStrings.ScanInstructions, Padding = new Thickness(24, 16) };
    private CameraBarcodeReaderView? camera;
    private int scanned;

    private TransferScanPage(Action<Login> imported)
    {
        this.imported = imported;
        Title = AppStrings.ScanTitle;
        ReaderTheme.Apply(this);
    }

    /// <summary>Asks for camera access only now, when the user has chosen to scan.</summary>
    public static async Task OpenAsync(Page owner, Action<Login> imported)
    {
        var permission = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
            permission = await Permissions.RequestAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
        {
            await owner.DisplayAlertAsync(AppStrings.ScanCameraDeniedTitle, AppStrings.ScanCameraDenied, AppStrings.AlertOkButton);
            return;
        }
        await owner.Navigation.PushAsync(new TransferScanPage(imported));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        scanned = 0;
        camera = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions { Formats = BarcodeFormat.QrCode, AutoRotate = true, Multiple = false },
            IsDetecting = true
        };
        camera.BarcodesDetected += Detected;
        var layout = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)] };
        layout.Add(status, 0, 0);
        layout.Add(camera, 0, 1);
        Content = layout;
        ReaderTheme.Apply(this);
    }

    protected override void OnDisappearing()
    {
        StopCamera();
        base.OnDisappearing();
    }

    private void StopCamera()
    {
        if (camera is null) return;
        camera.IsDetecting = false;
        camera.BarcodesDetected -= Detected;
        camera.Handler?.DisconnectHandler();
        camera = null;
        Content = status;
    }

    private void Detected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results.FirstOrDefault()?.Value;
        if (value is null) return;
        if (!CredentialTransfer.IsTransferCode(value))
        {
            MainThread.BeginInvokeOnMainThread(() => status.Text = SharedStrings.ErrorTransferNotObkCode);
            return;
        }
        if (Interlocked.Exchange(ref scanned, 1) == 1) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            StopCamera();
            Navigation.InsertPageBefore(new TransferUnlockPage(value.Trim(), imported), this);
            await Navigation.PopAsync();
        });
    }
}

/// <summary>Decrypts a scanned code with the words shown on the PC; nothing is signed in or saved here.</summary>
internal sealed class TransferUnlockPage : ContentPage
{
    private readonly string code;
    private readonly Action<Login> imported;
    private readonly Entry words = new()
    {
        IsTextPredictionEnabled = false, IsSpellCheckEnabled = false,
        Keyboard = Keyboard.Create(KeyboardFlags.None), ReturnType = ReturnType.Done
    };
    private readonly Button unlock = new() { Text = AppStrings.UnlockButton };
    private readonly ActivityIndicator progress = new();
    private readonly Label status = new();
    private bool busy;

    public TransferUnlockPage(string code, Action<Login> imported)
    {
        this.code = code;
        this.imported = imported;
        Title = AppStrings.UnlockTitle;
        unlock.Clicked += async (_, _) => await UnlockAsync();
        words.Completed += async (_, _) => await UnlockAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                Children =
                {
                    new Label { Text = AppStrings.UnlockInstructions, FontSize = 18 },
                    new Label { Text = AppStrings.UnlockWordsLabel }, words,
                    unlock, progress, status,
                    new Label { Text = AppStrings.UnlockHint, FontSize = 13 }
                }
            }
        };
        ReaderTheme.Apply(this);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        words.Focus();
    }

    private async Task UnlockAsync()
    {
        if (busy) return;
        busy = true;
        unlock.IsEnabled = words.IsEnabled = false;
        progress.IsRunning = true;
        status.Text = AppStrings.UnlockWorking;
        try
        {
            var passphrase = words.Text ?? "";
            var login = await Task.Run(() => CredentialTransfer.Open(code, passphrase));
            words.Text = "";
            imported(login);
            await Navigation.PopAsync();
        }
        catch (ObkException error)
        {
            status.Text = error.Message;
            await DisplayAlertAsync(AppStrings.UnlockFailedTitle, error.Message, AppStrings.AlertOkButton);
        }
        finally
        {
            busy = false;
            unlock.IsEnabled = words.IsEnabled = true;
            progress.IsRunning = false;
        }
    }
}
