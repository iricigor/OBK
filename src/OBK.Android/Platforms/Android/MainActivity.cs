using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace OBK.AndroidApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
        ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int UpdateRequest = 7401;
    private TaskCompletionSource<Result>? updateResult;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        if (savedInstanceState is null && OpensInbox(Intent)) App.RequestInbox();
        base.OnCreate(savedInstanceState);
    }

    protected override async void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (!OpensInbox(intent) || Microsoft.Maui.Controls.Application.Current is not App app) return;
        App.RequestInbox();
        await app.ShowRequestedInboxAsync();
    }

    // A check notification was tapped; reopening OBK from Recents repeats its intent and is ignored.
    private static bool OpensInbox(Intent? intent) => intent?.Action == CheckNotifier.OpenInboxAction &&
        !intent.Flags.HasFlag(ActivityFlags.LaunchedFromHistory);

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
