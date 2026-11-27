using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using OBK.Core;

namespace OBK.AndroidApp;

/// <summary>Hourly background checks with WorkManager; Android may postpone a run, for example to save battery.</summary>
internal static class ScheduledCheck
{
    internal const string LogTag = "OBK.Check";
    private const string WorkName = "obk_scheduled_check";

    /// <summary>Enqueues the hourly check, keeping an existing schedule, or cancels it.</summary>
    internal static void Schedule(bool enabled)
    {
        var manager = WorkManager.GetInstance(global::Android.App.Application.Context);
        if (!enabled)
        {
            manager.CancelUniqueWork(WorkName);
            return;
        }
        var constraints = new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build();
        var request = PeriodicWorkRequest.Builder.From<ScheduledCheckWorker>(TimeSpan.FromHours(1))
            .SetConstraints(constraints).Build();
        manager.EnqueueUniquePeriodicWork(WorkName, ExistingPeriodicWorkPolicy.Keep!, request);
    }

    /// <summary>Downloads every child with a saved password like Refresh, then posts the enabled notifications.</summary>
    internal static async Task RunAsync()
    {
        if (!ReaderPreferences.ScheduledChecks || ReaderSession.Syncing || !File.Exists(ReaderSession.DatabasePath)) return;
        await App.ApplySavedLanguageAsync();
        var store = new LocalStore(ReaderSession.DatabasePath, new AndroidDataProtection(hasStoredData: true));
        var checks = new List<ChildCheck>();
        var changed = false;
        foreach (var login in store.KnownLogins())
        {
            var child = ReaderPage.AccountLabelConverter.Label(login);
            try
            {
                if (await ReaderSession.CheckAsync(store, login) is not { } result)
                {
                    checks.Add(new(login, child, CheckOutcome.SignInNeeded, []));
                    continue;
                }
                checks.Add(new(login, child, CheckOutcome.Checked, ScheduledCheckReport.Arrivals(result.Before, result.After, result.Opened)));
                changed |= ScheduledCheckReport.Changed(result.Before, result.After);
            }
            // Any failure is named in the notification, and the remaining children are still checked.
            catch (Exception error)
            {
                global::Android.Util.Log.Warn(LogTag, $"A child could not be checked: {error.GetType().FullName}");
                checks.Add(new(login, child, CheckOutcome.Failed, []));
            }
        }
        if (changed && Application.Current is App app)
            MainThread.BeginInvokeOnMainThread(async () => await app.ArchivesChangedAsync());
        CheckNotifier.Post(checks);
    }
}

/// <summary>Runs one scheduled check. The fixed Java name keeps work enqueued by earlier installations runnable.</summary>
[Register("cz/obk/reader/ScheduledCheckWorker")]
public sealed class ScheduledCheckWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    public override ListenableWorker.Result DoWork()
    {
        // DoWork already runs on a background thread; a failed check must not take down the app process.
        try
        {
            ScheduledCheck.RunAsync().GetAwaiter().GetResult();
            return ListenableWorker.Result.InvokeSuccess();
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Warn(ScheduledCheck.LogTag, $"The scheduled check stopped: {error.GetType().FullName}");
            return ListenableWorker.Result.InvokeFailure();
        }
    }
}
