using Android.App;
using Android.Content;
using Android.Text;
using Android.Text.Style;
using AndroidX.Core.App;
using OBK.AndroidApp.Localization;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

/// <summary>Local notifications after scheduled checks; OBK has no server, so the phone raises them itself.</summary>
internal static class CheckNotifier
{
    internal const string OpenInboxAction = "cz.obk.reader.OPEN_INBOX";
    private const string NewMessagesChannel = "new_messages";
    private const string StatusChannel = "check_status";
    private const int StatusId = 1;
    private const string LastIdPreference = "scheduled_check_last_notification";

    private static Context Context => global::Android.App.Application.Context;

    /// <summary>False while OBK's notifications are blocked, including a denied or revoked Android 13 permission.</summary>
    internal static bool Enabled => NotificationManagerCompat.From(Context)!.AreNotificationsEnabled();

    /// <summary>Asks for the Android 13+ notification permission; called only when the user turns notifications on.</summary>
    internal static async Task<bool> RequestAsync()
    {
        try
        {
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted &&
                await Permissions.RequestAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                return false;
        }
        catch (PermissionException) { return false; }
        return Enabled;
    }

    /// <summary>Opens OBK's notification settings, or its app details where that page is unavailable.</summary>
    internal static void OpenSettings()
    {
        try
        {
            using var intent = new Intent(global::Android.Provider.Settings.ActionAppNotificationSettings);
            intent.PutExtra(global::Android.Provider.Settings.ExtraAppPackage, Context.PackageName);
            if (Platform.CurrentActivity is { } activity) activity.StartActivity(intent);
            else
            {
                intent.AddFlags(ActivityFlags.NewTask);
                Context.StartActivity(intent);
            }
        }
        catch (ActivityNotFoundException) { AppInfo.ShowSettingsUI(); }
    }

    /// <summary>Posts the summary or status for a finished check, if the user's settings ask for one.</summary>
    internal static void Post(IReadOnlyList<ChildCheck> checks)
    {
        if (!ReaderPreferences.CheckNotifications || !Enabled) return;
        var time = DateTimeOffset.Now.ToString(SharedStrings.DateTimeFormat, LanguageSettings.Culture);
        if (ScheduledCheckReport.Compose(checks, ReaderPreferences.EmptyCheckNotifications, time, Texts()) is not { } notification)
            return;
        var manager = NotificationManagerCompat.From(Context)!;
        CreateChannels(manager);
        var channel = notification.IsStatus ? StatusChannel : NewMessagesChannel;
        var style = new NotificationCompat.InboxStyle();
        foreach (var line in notification.Lines) style.AddLine(Styled(line));
        // Shown instead on a lock screen that hides sensitive content: the title, without children or subjects.
        var lockScreen = Builder(channel, notification.Title);
        var builder = Builder(channel, notification.Title);
        builder.SetContentText(notification.Lines.FirstOrDefault()?.Text);
        builder.SetStyle(style);
        builder.SetPublicVersion(lockScreen.Build());
        var built = builder.Build()!;
        if (notification.IsStatus)
        {
            manager.Notify(StatusId, built);
            return;
        }
        manager.Cancel(StatusId);
        var id = Preferences.Default.Get(LastIdPreference, StatusId) + 1;
        Preferences.Default.Set(LastIdPreference, id);
        manager.Notify(id, built);
    }

    private static NotificationCompat.Builder Builder(string channel, string title)
    {
        var builder = new NotificationCompat.Builder(Context, channel);
        builder.SetSmallIcon(Resource.Drawable.obk_notification);
        builder.SetColor(global::Android.Graphics.Color.ParseColor("#0F6CBD").ToArgb());
        builder.SetContentTitle(title);
        builder.SetContentIntent(OpenInbox());
        builder.SetAutoCancel(true);
        return builder;
    }

    private static void CreateChannels(NotificationManagerCompat manager)
    {
        foreach (var (id, importance, name, description) in new[]
        {
            (NewMessagesChannel, NotificationManagerCompat.ImportanceDefault,
                AppStrings.NotificationChannelNewMessages, AppStrings.NotificationChannelNewMessagesDescription),
            (StatusChannel, NotificationManagerCompat.ImportanceLow,
                AppStrings.NotificationChannelStatus, AppStrings.NotificationChannelStatusDescription)
        })
        {
            var channel = new NotificationChannelCompat.Builder(id, importance);
            channel.SetName(name);
            channel.SetDescription(description);
            manager.CreateNotificationChannel(channel.Build()!);
        }
    }

    private static PendingIntent? OpenInbox()
    {
        var intent = new Intent(Context, typeof(MainActivity));
        intent.SetAction(OpenInboxAction);
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(Context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    private static SpannableString Styled(CheckLine line)
    {
        var text = new SpannableString(line.Text);
        var start = line.Child is null ? -1 : line.Text.IndexOf(line.Child, StringComparison.Ordinal);
        if (start >= 0)
            text.SetSpan(new StyleSpan(global::Android.Graphics.TypefaceStyle.Bold), start, start + line.Child!.Length, SpanTypes.ExclusiveExclusive);
        return text;
    }

    private static CheckTexts Texts() => new(
        AppStrings.NotificationNewMessagesOne, AppStrings.NotificationNewMessagesFew, AppStrings.NotificationNewMessagesMany,
        AppStrings.NotificationArrival, AppStrings.NotificationMoreOne, AppStrings.NotificationMoreFew, AppStrings.NotificationMoreMany,
        AppStrings.NotificationNotCheckedSignIn, AppStrings.NotificationNotCheckedFailed, AppStrings.NotificationNoNewTitle,
        AppStrings.NotificationNotCheckedTitle, AppStrings.NotificationChecked, AppStrings.NotificationListAnd);
}
