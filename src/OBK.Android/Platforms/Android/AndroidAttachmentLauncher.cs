using Android.Content;
using OBK.AndroidApp.Localization;
using OBK.Core;

namespace OBK.AndroidApp;

internal static class AndroidAttachmentLauncher
{
    internal static void Open(string path, string mimeType)
    {
        var activity = Platform.CurrentActivity ?? throw new ObkException(AppStrings.ErrorNoAppForAttachment);
        try
        {
            using var file = new Java.IO.File(path);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, $"{activity.PackageName}.attachments", file);
            using var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, mimeType);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            activity.StartActivity(intent);
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            throw new ObkException(AppStrings.ErrorNoAppForAttachment, error);
        }
    }
}

[ContentProvider(["cz.obk.reader.attachments"], Exported = false, GrantUriPermissions = true)]
[global::Android.App.MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/attachment_paths")]
public sealed class AttachmentFileProvider : AndroidX.Core.Content.FileProvider;
