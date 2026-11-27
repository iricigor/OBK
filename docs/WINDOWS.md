# OBK on Windows

[Back to README](../README.md#install)

## Install

1. Open the [latest release](https://github.com/iricigor/OBK/releases/latest).
2. Download `OBK-<version>-windows-x64-setup.exe`.
3. Run the installer and start **OBK** from the Start menu. Administrator
   rights and a separate .NET installation are not normally required.

OBK checks for updates while it runs and offers **Update and restart** when a
newer version is ready.

> **Unsigned package:** current Windows releases are not yet code-signed.
> SmartScreen or Windows may warn that the publisher is unknown. Download only
> from the official repository's Releases page, and check the published
> checksums and signing status before approving installation.

![OBK on Windows in light mode](images/obk-main.png)

## Marks

The marks feature on the #93 development branch adds **Marks** to navigation.
Choose **Subjects** or **Recent marks**, filter by child, and open an assessment
for details. Refresh marks separately from messages; successful snapshots are
available offline. See [marks behavior and limitations](features/MARKS.md).

## Preview and source builds

The Actions [Windows workflow](../.github/workflows/windows-build.yml) produces
installer artifacts for pull-request verification. Source builds and packaging
instructions are in the
[technical documentation](TECHNICAL.md#windows-installer-and-releases).

Windows requires Windows 10 or later. To remove the app, uninstall **OBK** from
Windows **Apps > Installed apps**.
