# OBK Android preview

[Back to README](../README.md#install)

The Android reader supports sign-in, multiple children, refresh, offline
folders and search, and opening or sharing downloaded attachments. It requires
Android 10 or later and is currently a preview.

![Android message list in light mode](images/android-light-messages.png)

## Install a release APK

1. Open the [latest release](https://github.com/iricigor/OBK/releases/latest)
   on the phone and download `OBK-<version>-android.apk`.
2. Open the downloaded APK. If Android asks, allow installation from that
   browser or file manager for this one installation.
3. Review the warning and confirm installation.

> **Preview signing:** Android preview APKs are development-signed, not signed
> by a trusted public app-store/release identity. Android may block them or show
> an unknown-app warning. Download only from the official repository; do not
> grant installation access to untrusted download sources generally.

Install a newer compatible release APK over the existing release app to keep
accounts and offline archives. The updater inside OBK verifies checksum,
package, version, and signing compatibility before opening Android's installer.

## Install a preview workflow artifact

Development-signed APKs from [Android preview runs](../.github/workflows/android-build.yml)
are intended for device verification, not production use. Installing a preview
built with a different key may require uninstalling the previous preview, which
deletes that preview's local archive and encryption key.

Do not uninstall a stable release merely to update it. If you move from a
development-signed preview to a release, first expect to set up accounts again
unless Android accepts the APK's signing identity as unchanged.

See [Android build and verification](TECHNICAL.md#android-preview) for
maintainer setup and [release signing](TECHNICAL.md#android-release-signing)
for stable distribution details.
