using Android.App;
using Android.Content;
using Android.Content.PM;

namespace OBK.AndroidApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
        ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int UpdateRequest = 7401;
    private TaskCompletionSource<Result>? updateResult;

    internal async Task<Result> RunForResultAsync(Intent intent)
    {
        if (updateResult is not null) throw new InvalidOperationException("An Android update action is already open.");
        var result = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        updateResult = result;
        try
        {
            StartActivityForResult(intent, UpdateRequest);
            return await result.Task;
        }
        finally { updateResult = null; }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == UpdateRequest) updateResult?.TrySetResult(resultCode);
    }

    protected override void OnDestroy()
    {
        updateResult?.TrySetCanceled();
        base.OnDestroy();
    }
}
