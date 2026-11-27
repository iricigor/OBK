using Android.App;
using Android.Content;
using Android.Content.PM;
using OBK.AndroidApp.Localization;
using OBK.Core;

namespace OBK.AndroidApp;

internal enum AndroidUpdateResult
{
    Installed,
    NotInstalled,
    PermissionDeclined
}

internal static class AndroidUpdateInstaller
{
    internal static Version InstalledVersion =>
        UpdateService.TryParseVersion(AppInfo.Current.VersionString, out var version)
            ? version : throw new ObkException(AppStrings.UpdateInvalidInstalledVersion);

    internal static void Validate(string path, AvailableUpdate update)
    {
        var context = global::Android.App.Application.Context;
        var manager = context.PackageManager ?? throw new ObkException(AppStrings.UpdateInvalidPackage);
        using var installed = manager.GetPackageInfo(context.PackageName!, PackageInfoFlags.SigningCertificates)
            ?? throw new ObkException(AppStrings.UpdateInvalidPackage);
        // Some Android archive parsers collect certificates only when GET_SIGNATURES is also requested.
#pragma warning disable CS0618
        using var archive = manager.GetPackageArchiveInfo(path, PackageInfoFlags.SigningCertificates | PackageInfoFlags.Signatures);
#pragma warning restore CS0618
        if (archive is null || archive.PackageName != installed.PackageName ||
            archive.VersionName != $"1.0.{update.Version.Build}" ||
            archive.LongVersionCode != (long)update.Version.Build + 1 ||
            archive.LongVersionCode <= installed.LongVersionCode)
            throw new ObkException(AppStrings.UpdateInvalidPackage);
        var currentSigners = installed.SigningInfo?.GetApkContentsSigners();
        var newSigners = archive.SigningInfo?.GetApkContentsSigners();
        if (currentSigners is not { Length: > 0 } || newSigners is not { Length: > 0 } ||
            currentSigners.Length != newSigners.Length ||
            currentSigners.Any(signer => !newSigners.Any(candidate => signer.Equals(candidate))))
            throw new ObkException(AppStrings.UpdateIncompatibleSigner);
    }

    internal static async Task<AndroidUpdateResult> InstallAsync(string path, AvailableUpdate update, Func<Task<bool>> explainPermission)
    {
        Validate(path, update);
        var activity = Platform.CurrentActivity as MainActivity ?? throw new ObkException(AppStrings.UpdateInstallerUnavailable);
        try
        {
            if (!activity.PackageManager!.CanRequestPackageInstalls())
            {
                if (!await explainPermission()) return AndroidUpdateResult.PermissionDeclined;
                using var permission = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                    global::Android.Net.Uri.Parse($"package:{activity.PackageName}"));
                await activity.RunForResultAsync(permission);
                if (!activity.PackageManager.CanRequestPackageInstalls())
                    throw new ObkException(AppStrings.UpdatePermissionDenied);
            }
            // Recheck after returning from Android settings; never hand off an unchecked cached file.
            Validate(path, update);
            using var file = new Java.IO.File(path);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, $"{activity.PackageName}.updates", file);
            using var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            intent.PutExtra(Intent.ExtraReturnResult, true);
            await activity.RunForResultAsync(intent);
            return InstalledVersion >= update.Version
                ? AndroidUpdateResult.Installed
                : AndroidUpdateResult.NotInstalled;
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            throw new ObkException(AppStrings.UpdateInstallerUnavailable, error);
        }
    }

    internal static bool OpenSecuritySettings()
    {
        if (Platform.CurrentActivity is not MainActivity activity) return false;
        return TryOpenSettings(activity, global::Android.Provider.Settings.ActionSecuritySettings) ||
            TryOpenSettings(activity, global::Android.Provider.Settings.ActionSettings);
    }

    private static bool TryOpenSettings(MainActivity activity, string action)
    {
        using var intent = new Intent(action);
        try
        {
            activity.StartActivity(intent);
            return true;
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            return false;
        }
    }
}

[ContentProvider(["cz.obk.reader.updates"], Exported = false, GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/update_paths")]
public sealed class UpdateFileProvider : AndroidX.Core.Content.FileProvider;
