using ZXing.Net.Maui.Controls;

namespace OBK.AndroidApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() =>
        MauiApp.CreateBuilder().UseMauiApp<App>().UseBarcodeReader().Build();
}
