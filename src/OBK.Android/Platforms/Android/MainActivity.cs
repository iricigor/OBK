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
    private const string HandledNotificationState = "obk_handled_notification";
    private TaskCompletionSource<Result>? updateResult;
    private string? handledNotification;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        handledNotification = savedInstanceState?.GetString(HandledNotificationState);
        if (savedInstanceState is null || (Intent?.DataString ?? CheckNotifier.OpenInboxAction) != handledNotification)
            RequestNotification(Intent);
        base.OnCreate(savedInstanceState);
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        outState.PutString(HandledNotificationState, handledNotification);
        base.OnSaveInstanceState(outState);
    }

    protected override async void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (Microsoft.Maui.Controls.Application.Current is not App app || !RequestNotification(intent)) return;
        Intent = intent;
        await app.ShowRequestedNotificationAsync();
    }

    // A check notification was tapped; reopening OBK from Recents repeats its intent and is ignored.
    private static bool OpensInbox(Intent? intent) => intent?.Action == CheckNotifier.OpenInboxAction &&
        !intent.Flags.HasFlag(ActivityFlags.LaunchedFromHistory);

    private bool RequestNotification(Intent? intent)
    {
        if (!OpensInbox(intent) || Destination(intent!) is not { } destination) return false;
        handledNotification = intent!.DataString ?? CheckNotifier.OpenInboxAction;
        App.RequestNotification(destination);
        return true;
    }

    private static NotificationDestination? Destination(Intent intent)
    {
        try
        {
            return NotificationDestination.FromPayload(intent.GetStringArrayExtra(CheckNotifier.AccountsExtra),
                intent.GetStringExtra(CheckNotifier.FolderExtra));
        }
        catch (InvalidDataException)
        {
            global::Android.Util.Log.Warn(ScheduledCheck.LogTag, "A notification had an invalid destination; ignoring its navigation request.");
            return null;
        }
    }

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
