# OBK technical documentation

Developer, maintainer, and security details for OBK (Offline Bakaláři Komens).
For an overview and installation, see the [README](../README.md). Potential
future work and its prerequisites are recorded in [Deferred ideas](../DEFERRED_IDEAS.md).

## GitHub Pages user guide

The guide is a dependency-free static HTML/CSS site in `docs\user-guide`.
Its initial scope is three pages: overview, Windows getting started, and
Android getting started. Detailed screen/folder guides and light/dark
screenshots are a separate follow-up. No app build or site-generation step is
needed.

Stable public destinations:

- Overview: <https://iricigor.github.io/OBK/>
- Windows start: <https://iricigor.github.io/OBK/windows.html>
- Android start: <https://iricigor.github.io/OBK/android.html>

Keep these destinations when adding further guide pages: the app's follow-up
help menus use the platform URLs. Use relative paths for guide navigation and
assets so they work under the `/OBK/` Pages prefix. Links to source documents
outside the published guide must use their GitHub URLs.

Run the focused checks from the repository root with Python 3.10 or later:

```powershell
python -m unittest discover -s .\tools -p "test_check_user_guide.py"
python .\tools\check_user_guide.py
```

Preview locally:

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory .\docs
```

Open <http://127.0.0.1:8765/user-guide/>. Navigate to both platform pages, check
a narrow phone-sized viewport and keyboard navigation, and switch the browser
or OS light/dark preference. The site follows that preference without scripts,
external fonts, or analytics. The local preview also exercises navigation from
a subdirectory rather than only the server root.

In repository **Settings > Pages**, set **Source** to **GitHub Actions**.
The `User guide` workflow validates required routes, local pages/assets,
fragments, and page metadata on guide-related PRs. It uploads only
`docs\user-guide`, and deploys only after a matching push to `main` (or a manual
run selected on `main`). PRs and manual runs on other branches cannot publish.
The deploy job alone has `pages: write` and `id-token: write`; action versions
are pinned to commit hashes. No deployment secret is required.

Selecting GitHub Actions as the source does not itself publish a site. The
first publication needs the workflow on `main` and a successful deployment.
After deployment, verify the workflow's URL and all three public routes before
treating live publication as complete. The checker does not fetch external
URLs, so release and source-document links also need a manual review.

## Documentation screenshots

`docs\images\obk-main.png` is rendered from fictional sample data, without
contacting any school. The Windows and Android light/dark examples in
`docs\images` must also use fictional data and must not reveal school account
details. Regenerate the primary Windows image with a release-style build so
the local development banner is not included:

```powershell
dotnet build .\src\OBK.App\OBK.App.csproj -c Release -p:LocalBuild=false
.\src\OBK.App\bin\Release\net10.0-windows\OBK.exe --screenshot "$PWD\docs\images\obk-main.png"
```

The command uses a temporary archive that is deleted afterwards.

Use `--marks-screenshot <path.png>` instead to capture fictional marks in
Subjects, Recent marks, dark mode and both list densities. The additional
captures use `.recent.png`, `.dark.png`, `.compact.png` and
`.compact-subjects.png` suffixes. This also uses a temporary encrypted
archive and never contacts a school.

## School directory maintenance

`data\schools.json.gz` is a gzip-compressed UTF-8 JSON snapshot of the public
[Bakaláři directory](https://sluzby.bakalari.cz/api/v1/municipality). The Windows
application embeds the snapshot and uses it for offline school search in the
sign-in window; choosing a result populates the HTTPS base URL. Search matches
school names, town associations, and school-specific URL text. Results appear
only when fewer than seven schools match, and manual URL entry remains
available. There are no startup downloads or scheduled refreshes.

The decoded format contains `formatVersion` (currently 1), `source`, `fetchedAt`
(UTC), `municipalityCount`, `expectedSchoolCount` (named groups only),
`schoolCount` (unique collected records), `invalidUrlCount`, `complete`,
`excludedMunicipalities`, `diagnostics`, `municipalities`, and `schools`.
Each municipality has its upstream `name` and `schoolCount`, `retrieved`, and
`returnedSchoolCount` (usable records before deduplication). Each school has
`id`, `name`, the exact upstream `schoolUrl`, `urlValid`, and
`municipalityMatches`. Lookups can include neighboring towns: these matches are
query associations, not verified geographic assignments. Schools are deduplicated
by ID. Conflicting duplicates retain the first record in stable municipality
order and produce a diagnostic.

URLs are directory data, not verified API availability. Legacy HTTP URLs and
malformed URL strings are retained verbatim, not repaired or contacted; malformed
values have `urlValid: false`.

The upstream index includes an unnamed municipality whose schools cannot be
retrieved through its lookup API. This agreed exclusion is recorded with its
advertised count and reason in `excludedMunicipalities`, not silently discarded.
Refreshes are best-effort: individual lookup failures and malformed school
records are skipped, with warnings and metadata diagnostics. A second index
check detects changes during collection, but failure of this recheck does not
discard usable records. The unique total is compared with the index's advertised
named-group count; mismatches produce diagnostics, not a failed refresh.
`complete` is false if there are diagnostics, exclusions, or malformed URLs.
This cannot establish that the upstream directory itself lists every school
using Bakaláři.

The initial index must be valid and at least one usable school must be collected;
otherwise the refresh fails and preserves the previous snapshot. A partial
refresh may contain fewer schools than the previous snapshot: inspect coverage
and diagnostics in the update PR before merging. Failed towns are not backfilled
with stale records.

Maintainers can run **📚 School directory refresh** from GitHub Actions using
**Run workflow**. It refreshes from `main`, commits only changed compressed data
to a new maintenance branch, and opens a pull request; it never commits directly
to `main`. Review or close a pending directory-update PR before running again.
Enable **Allow GitHub Actions to create and approve pull requests** in the
repository's **Settings > Actions > General > Workflow permissions**. No school
credentials or additional secret are needed.

PRs created with `GITHUB_TOKEN` do not automatically trigger other workflows.
Run **🧪 Validation** manually on the maintenance branch and review its result
before merging. A failed publication step reports an error; inspect any pushed
maintenance branch before retrying.

Local maintenance needs Python 3.10 or later, with no third-party dependencies
(Python remains development tooling, not an application runtime):

```powershell
python -m unittest discover -s .\tools -p "test_school_directory.py"
python .\tools\refresh_school_directory.py
```

The tool uses four concurrent requests with 30-second timeouts, performs no
school-login requests, and writes the compressed file atomically after collection.
An unchanged directory and diagnostics preserve the previous retrieval timestamp
and bytes, so merely running a refresh does not produce an update PR.

To inspect the snapshot locally without saving a plaintext duplicate:

```powershell
python -c "import gzip,json; d=json.load(gzip.open(r'data\schools.json.gz','rt',encoding='utf-8')); print(d['fetchedAt'], d['schoolCount'], d['municipalityCount']); print(d['excludedMunicipalities'])"
```

Gzip hides school names from basic plaintext crawling, but it is **not encryption
or access control**. Anyone with the file can decompress it. Do not publish a
plaintext duplicate alongside it; compression does not hide names in upstream
public sources or elsewhere on a website.

## Automatic updates

On Windows, OBK checks the latest stable [GitHub release](https://github.com/iricigor/OBK/releases)
one minute after an account is opened, then every five minutes. The status bar shows
the installed version when current. A newer installer is downloaded in the
background and verified against the release's `SHA256SUMS.txt`; a failed
check or download shows a message and is retried at the next interval. Once ready,
**Update and restart** installs it silently and reopens OBK. Its dropdown offers
**Ignore during this session** and **Install when I exit**. Clicking **You're on the
latest version** (or a check error), or **Settings > About > Check now**, checks again
immediately. There is no setting to
disable checks yet; only HTTPS assets from this repository's releases are used.

## Application icon

The **Bold box** icon represents messages kept locally: a flat blue tile, solid
white archive box, separated lid, and a large envelope emblem.
`tools\make_app_icon.py` generates both `src\OBK.App\Assets\OBK.ico` for the Windows
executable, windows, and installer, and
`src\OBK.Android\Resources\AppIcon\obk.png` for the Android launcher and splash.
Both platforms use the same geometry and the interface's blue accent (`#0F6CBD`).
Windows includes 16, 20, 24, 32, 40, 48, 64, 128, and 256 px frames; frames up to
32 px use pixel-aligned shapes and a single blue V on the white box, omitting the
inset envelope panel to keep the title-bar and taskbar icon clear. Larger frames
retain the detailed emblem. The 512 px Android image leaves
space around the box so circular launcher masks do not clip the white artwork.

Regenerate and check both assets with Pillow installed:

```powershell
python .\tools\make_app_icon.py
python -m unittest discover -s .\tools -p "test_make_app_icon.py"
```

## Main-window frame

The main window retains its custom WPF title bar. On Windows 11 (build 22000
or later), it requests `DWMWCP_ROUND` through `DWMWA_WINDOW_CORNER_PREFERENCE`
once its native handle exists. A one-DIP `WindowChrome.GlassFrameThickness`
keeps WPF from installing a window region, which would block native rounding.
The existing opaque content stays in place; no per-pixel transparency or
manual window-region clipping is used.

Windows controls the final appearance: restored windows can be rounded,
while maximized/snapped windows and some virtualized or remote environments
remain square. Native API failures are reported in the status bar after
initialization, without preventing offline use. Windows 10 retains the
existing frame, and login/prompt dialogs are unchanged.

## Message-reader surfaces

The Windows reader uses the blue-shades treatment tracked in #60: a
slate-blue subject/sender header, a lighter blue band for possibly related
messages, and the existing neutral body surface. The related band is hidden
when there are no matches. Both themes use dedicated dynamic brush resources
so theme switching preserves readable metadata and navigation links.
Message discovery, navigation, attachments, and reading-size behavior remain
unchanged. Attachments stay below related messages and above the body, on
their own neutral-shaded surface. This band is hidden when there are no files;
file cards and attachment actions are unchanged.

## Automatic refresh intervals

Both platforms offer Off, 15 minutes, 30 minutes, 1 hour, 4 hours, and 24 hours.
The shared `RefreshIntervals` helper validates the preset values and supplies
localized labels.

Windows saves `RefreshIntervalMinutes` in `appearance.json`; older files
default to Off. A window-owned dispatcher timer follows the saved interval and
is stopped on close, including window replacement during language changes.
Changing the interval restarts the timer without restarting the app. Checks run
only while OBK is open, including minimized, and use the selected child scope
and **Always refresh all children** setting. Automatic and manual refresh share
a guard acquired before the first asynchronous store read; automatic checks
skip busy operations and report missing passwords without opening sign-in.
Manual refresh retains its sign-in prompts. Off stops future timer ticks;
an already running refresh may finish.

Android persists `refresh_interval_minutes` separately from its existing
`scheduled_checks` enable preference. Enabled older installations retain the
1-hour interval and disabled installations stay Off. Android's approximate
background scheduling and notification behavior are described below.

## Initial sign-in downloads

Windows and Android show an inline **Login successful** confirmation immediately
after receiving a valid access token, separately from message progress. All
message folders remain foreground on the sign-in screen and are saved before
attachments begin. Only then, while an attachment download is active, does
**Download attachments in background** offer entry to the reader. The original
task, client and synchronization guard remain alive until files finish or fail;
there is no second message download or unobserved fire-and-forget task.

The reader shows a compact bottom strip with successfully cached/total unique
attachments, a reduced-motion-aware activity indication, and details. Android
also shows it while an individual message is open. Cache hits count as complete,
but an in-progress or failed file does not count until its encrypted transaction
commits. Open/share/save actions become available as individual files commit,
without changing the selected message or its opened state.

Authentication failure has no success confirmation. Message failure retains the
previous archive; attachment failure retains the newly saved messages and any
complete files, with distinct errors and retry guidance. Retry sign-in or
**Refresh** reuses cached files. Refresh keeps its existing account-selection and
all-or-nothing message-archive behavior.

“Background” is in-app reading only. Closing OBK or Android suspending it can
interrupt downloads; this flow does not schedule an OS background job. Progress
remains readable with system animations disabled.

## Interface text and localization

App-owned interface text lives in `.resx` resource sets. English is the neutral
language; Czech, Ukrainian, and German are also supported.

The Windows language button combines a fixed globe icon with the current
EN/CS/UK/DE code and cycles English → Czech → Ukrainian → German → English beside
the theme button, including sign-in. Android places EN/CS/UK/DE in the reader's
status footer and in the native
toolbar on settings, sign-in, and message pages. Language changes retain the
saved System/Light/Dark appearance choice and the open navigation stack.
The tooltip/accessibility
description names the next language. An explicit choice is stored in
`language.json` beside the Windows database or in Android's app-private data
directory. No account data or credentials are stored in this file.

Without a saved choice, the Windows display language or Android locale selects
English, Czech, Ukrainian, or German; any other language selects Czech. Missing
settings use OS detection; unreadable or invalid settings report an error rather
than silently ignoring corruption. A failed save reports an error and keeps the
current language so the selection can be retried.

Switching rebuilds the Windows window or Android navigation stack immediately,
without restarting or downloading school data. The account, folder, selected
message, search, and existing sort/group state are preserved, as are sign-in
form fields. Windows retains its update controller and appearance settings.
Rebuilt Windows windows stay DWM-cloaked until their first frame renders, and
the previous window closes only then, so switching does not flash a blank white
window.
Switching is unavailable during synchronization, archive loading, or attachment
operations. Display dates use shared translatable layouts; month group names
and numbers use the selected culture. API/storage formats and school content
remain unchanged. Resource lookup and formatting use the selected culture
explicitly, including asynchronous work started before a switch.
Date searches include all supported languages' layouts so switching does not
invalidate a query containing a month name or abbreviation from another
supported language.

| Resource set | Used by | Generated class |
| --- | --- | --- |
| `src\OBK.Core\Localization\SharedStrings.resx` | Errors, folder names, date groups, and other text shared by both apps | `OBK.Core.Localization.SharedStrings` |
| `src\OBK.App\Localization\AppStrings.resx` | Windows app only | `OBK.App.Localization.AppStrings` |
| `src\OBK.Android\Localization\AppStrings.resx` | Android app only | `OBK.AndroidApp.Localization.AppStrings` |

`src\Localization.targets` generates a public class with one property per key
before compilation, so a misspelled key fails the build. WPF uses
`{x:Static loc:AppStrings.Key}` with
`xmlns:loc="clr-namespace:OBK.App.Localization"`; C# uses the class directly.

Conventions:

- Keys are PascalCase and prefixed by area (`Login…`, `Settings…`, `Error…`).
- Text with inserted values uses composite placeholders (`{0}`, `{1:N0}`) and
  `Localized.Format(...)`, never concatenated fragments, because word order
  differs between languages. Each placeholder is described in a `<comment>`.
- Persisted or compared values (theme, sort and grouping options) remain
  invariant identifiers; only their display text is localized.
- School-provided content (messages, names, attachments), technical
  identifiers, and `--smoke-test` diagnostics are not localized.
- API requests and stored data stay culture-independent.

To add a language, add `<Name>.<culture>.resx` (for example
`AppStrings.uk.resx`) next to each neutral file, translating every key. Runtime
falls back to English for missing keys, so completeness is enforced by a
separate check instead. The **Localization resource completeness** job in
**🧪 Validation** requires Czech, Ukrainian, and German, discovers any
additional languages and resource sets, and fails on invalid XML, a language
missing from any resource set, missing or extra keys, duplicate keys, empty
values, or placeholders that differ from English. This required list also
catches removing every resource file for one supported language. Run it locally
with:

```powershell
python -m unittest discover -s .\tools -p "test_check_localization.py"
python .\tools\check_localization.py
```

## Purpose and scope

OBK is a read-only Bakaláři Komens client that downloads messages for offline
reading. Sending messages is out of scope.

The Windows client uses C#/.NET 10 LTS with WPF and SQLite storage, published
self-contained. Users need neither Python/Tkinter nor a separately installed
.NET runtime. WPF is Windows-only; planned macOS, Linux, Android, and iOS
releases will need a separate presentation implementation.

### Android implementation milestone

The Android reader is tracked in [#25](https://github.com/iricigor/OBK/issues/25).
The agreed target is Android 10 (API 29) or newer, using an Android-only .NET
MAUI application with shared C# core logic. Core portability and Android
Keystore-backed storage come first, followed by school sign-in, multiple
accounts, manual refresh, offline message folders/search, and downloaded
attachment viewing/export in the same milestone. Windows keeps its existing
DPAPI protection and behavior.

The first distribution target is an APK from GitHub Releases. Localization,
Play Store publishing, background sync, notifications, and Windows archive
import are excluded from this milestone. The implementation remains read-only
toward school servers and requires device verification before merge.

### Android preview

Android presentation changes use the Windows reader's hierarchy as a reference:
message content comes first, metadata and account management are secondary,
and colors have semantic light/dark roles. Keep Android-native navigation,
48-dp touch targets, and system text scaling rather than copying the desktop's
three-column layout. UI refinement does not imply adding missing desktop
features or implementing the separately tracked language controls.

The reader uses an integrated search bar above the list: the hamburger opens
a 280-dp navigation drawer headed **OBK / Offline Bakalari Komens** with a compact
**Children** section (**All children** first when more than one account exists)
and **Add child**, then **Inbox**, **Noticeboard**, **Sent**, and **Excuses**,
followed by **Manage accounts** and **Settings**. Child and folder rows show
blue new-message counts. The active folder is highlighted and announced in the drawer;
selecting a folder or child closes it. Account rows show a 40-dp avatar, name, school
where available, and active status. The 56-dp header avatar overlaps the left end
of the 48-dp search surface by 4 dp above and below; in the all-children scope it
shows up to three overlapping child avatars. The refresh icon follows the selected
child scope, unless **Always refresh all children** is enabled in Settings.
The avatar opens a normal chooser page with **All children** and each account's
identity row (avatar, name, school, username and active status), without management actions.

**All children** is the default scope and is remembered. It merges every account's
cached folder, shows each row's 40-dp child avatar and `Child · Sender`, and opens
messages and attachments with the owning account. Selecting a child filters to that
account. Message rows use 8-dp horizontal padding.

Search is an `Entry` with OBK's own icons, so the native SearchView magnifier is
gone. Focusing it hides the menu, avatar, and refresh, giving the field the whole
header; the back arrow or system Back clears and closes search. The keyboard action
is **Done** because results update 200 ms after typing stops, using the shared
Windows accent-insensitive matching on rendered message text, sender/recipient,
and localized dates. The sliders button shows the Group (None, Date, Child, Sender,
Subject) and Sort (Date, Sender, Subject) chips; ordering and date-group labels come
from the shared `MessageInsights.Arrange`. **Child** is Android-only and appears in
the all-children scope. Android defaults to Date grouping; both choices are saved.
Tapping a group header collapses or expands that group; the header shows a
chevron and the group's message count, and TalkBack reads the title, count and
state. Groups expand again when the folder, child scope, grouping, sort or
search changes, or the reader is recreated (restart or language switch);
**Refresh** and background reloads keep collapsed groups.

Messages in Inbox and Noticeboard not yet opened on this device show a bold subject,
blue date, and dot. Opening one records its ID in the encrypted `opened_messages`
table, per account; nothing is sent to the school. The first time an account with
a cached archive loads, all its existing messages are recorded as opened, so
upgrades and newly added accounts start with no new messages; only messages
arriving in later refreshes or scheduled checks are new. Sent and Excuses never show as new.
Tapping a row selects that account and returns to the reader; language switching
also rebuilds the chooser. Sync status stays below the list. Refresh summaries
expire after one minute using monotonic elapsed time; a new operation or leaving
the reader cancels the pending reset so it cannot overwrite newer state.
**Settings** groups one-tap segmented controls into **Appearance** (**System**,
**Light**, **Dark**), **Reading** (**Small / Medium / Large** reading sizes and
**Compact / Relaxed** list density), **Refresh** (**Always refresh all children**,
off by default and remembered across restarts), **Scheduled checks** (see
[Android scheduled checks and notifications](#android-scheduled-checks-and-notifications)),
**App updates**, and **About** (GitHub repository link). The list uses two lines: a
single-line, ellipsized subject and right-aligned short date, then an ellipsized
sender. Rows of messages with attachments show a paperclip on the sender line,
before the new-message dot, and TalkBack adds "Has attachments". Older years
remain visible in dates; full timestamps stay in the reader.
Both densities are tighter than the previous three-line layout; rows retain a
48-dp minimum touch height and grow with system font scaling. Reading sizes use
the desktop's 0.9 / 1.0 / 1.15 scale factors on Android's existing type sizes.
The single-line footer abbreviates the last sync to Today / Yesterday / 2-9
days ago plus the local time. Older syncs use a short date (including a year only
for previous years) plus time. The language control remains in the right-hand
auto-sized column; long status text truncates instead of pushing it off-screen.
Font and spacing resources update live and persist independently across restarts
and language changes. Medium / Relaxed are the defaults. List density also sets
the message header: **Relaxed** stacks subject, sender and date; **Compact** uses
two rows (subject; sender with the date and time on the right) and less padding.
Appearance applies immediately and persists in app-private preferences; this
non-sensitive UI preference is separate from encrypted school/account settings.
The child avatar reuses the desktop's six contrast-paired light/dark colors.
FNV-1a hashes the school URL plus username, so account ordering, renaming and
language changes do not change the assignment (color collisions are possible).
A one-word child name uses a 24-sp initial instead of 16 sp only when every
explicit child name is one word; school/account fallback labels are excluded
from that check and retain their normal size.
Message rows alternate between the surface and a pale theme-specific stripe on
both platforms. Stripes follow the current filtered/sorted message order,
continuing across Windows group boundaries. Selection and
hover take precedence over stripes on Windows. No other lists are striped.
On sign-in, school search is primary; **Enter school
address manually** reveals the editable HTTPS address (already visible for
an existing account). Message links and attachment actions stay explicit taps,
with privacy notices beneath the content. Appearance follows Android by default.
Opened messages reuse desktop's light/dark shaded header with contrasting
secondary text, plus a separate attachment band when files are present. The
message body remains on the normal reader surface. Below the header, the same
light-blue **Possibly related** band as on Windows lists likely previous and next
messages, found by the shared heuristic within the owning child's archive and
labelled with relative days (see [Windows desktop client](#windows-desktop-client)).
Each link is a single 48-dp row showing the direction, relative day and sender;
a second line shows the subject only when it differs from the open message's
subject, ignoring case, diacritics and reply or forward prefixes. TalkBack also
reads the exact date and the subject. The info button
explains that matches are heuristic. Tapping a link opens that message on top
with its owning account and marks it opened; Back returns. The band is hidden
when there are no matches and for Noticeboard messages.

The launcher icon uses `Resources\AppIcon\obk.png` declared as a `MauiIcon`.
The Android application manifest must reference MAUI's generated `@mipmap/obk`
and `@mipmap/obk_round` resources for normal and round launcher icons.
`src\OBK.Android\OBK.Android.csproj` targets `net10.0-android` with minimum API
29. `OBK.Core` also targets portable `net10.0`, and `LocalStore` accepts an
`ILocalDataProtection` implementation and an explicit database path. Its
Windows defaults and static DPAPI helpers remain compatible with existing
archives. Non-Windows callers cannot use the Windows defaults.

Android stores the database in the app-private data directory. An AES-256 key
is generated in Android Keystore; each encrypted value uses AES-GCM with a
fresh 12-byte nonce, an authentication tag, a version byte, and a fixed OBK
context authenticated as associated data. Settings, message payloads, attachment
metadata, and attachment chunks use this protection. Account identifiers and
archive index fields retain the shared database's existing layout; the whole
SQLite file is not encrypted. Hardware-backed key storage depends on the device.
Missing keys and failed authentication are explicit errors, never a reason to
create a replacement key for an existing database or store plaintext.

Device backup is disabled. Uninstalling deletes app data and its key.
Passwords are retained only with **Remember credentials**; use **Menu >
Manage accounts > Forget this account's saved password** to remove a saved password
without deleting the offline archive. API tokens stay in memory.
**Manage accounts** is a dedicated page, not an action sheet tied to the active
child. It lists all known accounts, marks the active account, shows school,
username and saved-password status, and exposes per-account sign-in and password
forgetting. Forgetting requires confirmation; no account or archive deletion is
offered. **Add child** stays fixed beneath the scrollable list. Avatar colors
and initials match the reader. Returning from sign-in reloads the account list;
language switching rebuilds this page while retaining the navigation stack.

Message HTML is parsed into native text rather than loaded into a browser.
Links open only on an explicit tap. Attachment opening/sharing creates a
decrypted file in the app-private `attachment-view` cache; these copies are
deleted on the next app start. **Open** sends Android an `ACTION_VIEW` intent
for that single file through OBK's dedicated attachment `FileProvider`, with a
temporary read grant; if no installed app handles it, OBK shows a localized
error. **Share** uses the system share sheet with the same MIME type. The
MIME type comes from the filename extension when it is on OBK's openable list
(which also controls whether **Open** is shown); otherwise OBK uses the
server's normalized type (parameters such as `charset` removed). It falls back
to `application/octet-stream` when that type is missing, invalid, generic, or
risky (HTML, SVG/XML, scripts, APK, executables). Recipient apps and
deliberately saved/shared copies are outside OBK's control. No Windows
`Zone.Identifier` stream is created on Android; Windows exports retain that
protection.

Install the .NET 10 SDK, then the Android workload and its dependencies:

```powershell
dotnet workload install maui-android
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "$env:LOCALAPPDATA\Microsoft\jdk-21"
dotnet build .\src\OBK.Android\OBK.Android.csproj -t:InstallAndroidDependencies `
  "-p:AndroidSdkDirectory=$env:ANDROID_HOME" "-p:JavaSdkDirectory=$env:JAVA_HOME" `
  -p:AcceptAndroidSdkLicenses=True
.\packaging\build_android_preview.ps1
```

The installer target accepts Android SDK licenses; review them before running.
The build script produces `dist\OBK-android-preview.apk` and
`dist\ANDROID-SHA256SUMS.txt`. Assemblies are embedded in the APK, so it can be
installed without .NET fast-deployment tooling. Trimming is disabled to preserve
the shared core's reflection-based JSON contracts in this initial reader.
Release AOT compilation is also disabled because the Android SDK requires
trimming for AOT; this keeps the same untrimmed core contracts in release APKs.

For a connected device, select its serial explicitly:

```powershell
& "$env:ANDROID_HOME\platform-tools\adb.exe" devices -l
& "$env:ANDROID_HOME\platform-tools\adb.exe" -s <serial> install -r .\dist\OBK-android-preview.apk
```

Verify school search/sign-in, multiple children, refresh, process restart and
offline reading, folders/search, attachment opening/sharing (a PDF opens in a
PDF-capable app, and an unsupported type shows an error without crashing),
missing-password
and network errors, system Back, dark theme, and enlarged system text.
Enter real credentials only on the device, never in issue comments or chat.

For developer-only visual verification, the Debug APK accepts the
`obk_verification` Boolean launch extra. It uses a separate
`verification.sqlite3` database with fictional accounts/messages/marks, runs actual
Keystore nonce/tamper and archive/attachment checks, and blocks school access.
Resolve the activity and launch it explicitly:

```powershell
$adb = "$env:ANDROID_HOME\platform-tools\adb.exe"
$activity = (& $adb -s <serial> shell cmd package resolve-activity --brief cz.obk.reader | Select-Object -Last 1).Trim()
& $adb -s <serial> shell am force-stop cz.obk.reader
& $adb -s <serial> shell am start -n $activity --ez obk_verification true
& $adb -s <serial> logcat -d -s OBK.Verify
```

The verification fixture and launch handling are excluded from Release builds.
The marks fixture includes a weighted subject, points-based assessment and empty
subject. Explicit assessment acknowledgement survives repeated verification
launches. Marks refresh is blocked in this mode and must leave the snapshot
intact.

On a shared emulator, build an isolated Debug package rather than replacing
an existing reader installation:

```powershell
dotnet build .\src\OBK.Android\OBK.Android.csproj `
  "-p:AndroidSdkDirectory=$env:ANDROID_HOME" "-p:JavaSdkDirectory=$env:JAVA_HOME" `
  -p:ApplicationId=cz.obk.marks.preview
& $adb -s <serial> install --no-incremental -r `
  .\src\OBK.Android\bin\Debug\net10.0-android\cz.obk.marks.preview-Signed.apk
$activity = (& $adb -s <serial> shell cmd package resolve-activity --brief cz.obk.marks.preview | Select-Object -Last 1).Trim()
& $adb -s <serial> shell am start -n $activity --ez obk_verification true
```

Attachment and update provider authorities follow `${applicationId}`, matching
the runtime package name so preview and release packages can coexist. Do not
uninstall the user's existing reader or capture private-school data for visual
verification.

To verify the initial download flow without school traffic, also pass
`--ez obk_verification_initial_download true`. This resets only the dedicated
fictional `verification-login.sqlite3` database and opens a prefilled example
sign-in page; enter any fictional password. The fixture pauses after
authentication (10 s), at 1/17 attachments (until the background button is
chosen, at most 60 s), and at 3/17 (60 s). To check the dark theme, switch the
device theme, for example with `adb shell cmd uimode night yes`.

To exercise saved-password status and forgetting in the dedicated accounts page,
add `--ez obk_verification_saved_password true` to the verification launch once.
This seeds a fictional password for Anna only in the isolated verification
database. Omit that extra on subsequent launches to verify that forgetting
persists; verification mode never contacts a school.
To exercise new-message markers, launch once with `--ez obk_verification true`
to create the opened-state baseline, then add `--ez obk_verification_new_message true`
on a later launch. That adds one fictional message for Tomas dated now; opening it
clears its marker, and the opened state persists across restarts.
Restart without the launch extra for real sign-in. Fictional data does not prove
compatibility with a real school's deployment.

The preview is development-signed and does not use the Windows update service.
Previews signed on different machines are not interchangeable; uninstalling
to switch keys loses local data. Production release signing is separate from
preview signing. No signing key or password belongs in Git.

### Android scheduled checks and notifications

**Settings > Scheduled checks** offers an **Automatic refresh interval** picker:
Off, 15 minutes, 30 minutes, 1 hour, 4 hours, or 24 hours. It replaces the
check-enable switch and retains its saved enabled/disabled state; existing enabled
checks default to 1 hour. **New-message notifications** needs scheduled checks;
**Also notify when nothing is new** needs notifications. Off disables and saves
both notification options as off. They remain off when checks are enabled again.
Turning notifications off also disables empty-check notifications. Disabled
options are greyed out until their parent is on again.

Checks use WorkManager unique periodic work (`obk_scheduled_check`) with the
saved interval, requiring any network connection. `ExistingPeriodicWorkPolicy.Update`
applies interval changes without duplicating jobs, interrupting running work, or
resetting its enqueue time. App start re-ensures the schedule; Off cancels it.
An in-flight download may finish, but no subsequent child is started once checks
are disabled. Android may postpone runs, for example in Doze or battery saver,
and may stop a run after about ten minutes, so timing is best-effort. The worker
(`cz.obk.reader.ScheduledCheckWorker`) opens the same encrypted database as the
app (never the verification database) and does nothing when it is missing. For
every child with a saved password, it downloads messages and attachments
exactly like **Refresh**, through the same endpoints; children without a saved
password are not checked. One process-wide lock keeps checks and foreground
downloads apart: a check skips its run while a download is in progress, and
**Refresh** or sign-in waits for a running check. A check reads each child's
saved password under that lock, just before downloading, and never changes saved
logins or the last used child, so a password forgotten during a check is neither
used nor saved again.

Arrivals are Inbox and Noticeboard messages that a check downloaded and that
are not opened in the reader. The opened-message baseline is recorded before
each download, so arrivals also stay new in the reader. After a school-year
change, the first check counts every unopened message of the new year; an
account's very first download is recorded as opened, like the reader's first
load, and announces nothing. A running app reloads its archives from the device
after a check that changed them.

With notifications on, a check with arrivals posts one summary for all children
on the **New messages** channel. A single arrival is titled `Child · Sender`
(only the child when no sender is supplied) with its subject as text. Several
arrivals have a plural-aware title and up to five `Child · Sender: Subject` lines
(`Child · Subject` without a sender), newest first; when more arrived, the last
line reads `+N more`. Children that could not be checked follow (sign-in needed
or check failed). The notification time is the newest arrival's sent time. Each
summary has its own ID, so earlier ones remain. With **Also notify
when nothing is new**, a check without arrivals instead posts a status on the
low-importance **Check status** channel, replacing the previous status. Texts use
the saved interface language. Titles and lines contain child names, senders, and
subjects; lock screens set to hide sensitive content show only the plural-aware
count title, without an avatar.

The monochrome OBK small/status-bar icon stays unchanged. Arrival notifications
also show a large icon rendered from the reader's existing initials, account
palette, and overlapping-avatar layout: the child's avatar for one child with
arrivals, or the all-children identity for multiple children with arrivals.
Children whose check failed do not affect that choice. Colors follow Android's
notification light/dark mode; status-only notifications keep the neutral icon.

Tapping an arrival notification opens only its affected accounts, in Inbox if
any arrival was in Inbox, otherwise Noticeboard. This includes arrivals omitted
from the five-line summary. A temporary reader scope supports a subset of several
children, with their names in the summary and their avatars in the child selector.
Explicitly choosing a child or **All children** leaves that temporary scope;
notification navigation does not overwrite the saved child preference. Status
notifications, and older notifications without a destination, open all-children
Inbox. Navigation reloads the device's offline archives without network access.

Each notification has its own immutable pending intent, keyed by notification
ID, so a newer check cannot replace an older card's destination. Its payload
contains the folder and SHA-256 account identities derived from school URL and
username, never passwords or child names; renaming children or duplicate names
cannot redirect a tap. Accounts since removed are excluded rather than widening
an arrival notification to unrelated children. Cold starts and taps while OBK
is running use the same destination; opening Recents does not replay a tap.
Notifications are local; OBK has no push service.

For manual verification, enable scheduled checks and notifications, then check
one child's Inbox arrivals, Noticeboard-only arrivals, and arrivals for two of
three saved children. Expect one card per run with the matching single/group
avatar, and a tap to open exactly the affected scope in the first arrival folder.
Post a later card and tap the older one to verify its destination is unchanged.
Repeat with OBK backgrounded and after stopping its process, with duplicate or
renamed child labels, in Android light/dark mode, and with lock-screen sensitive
content hidden. Check that status-only cards remain neutral and open all-children
Inbox, and that explicit child selection exits the temporary scope.

Android 13 and later require the `POST_NOTIFICATIONS` permission, which OBK
requests only when the user turns on **New-message notifications**. If it is
denied, or notifications are later blocked, the switch shows off with guidance
and an **Open Android settings** button for OBK's notification settings;
returning to Settings rechecks the state. WorkManager merges the network-state,
wake-lock, boot-completed and foreground-service permissions it needs into the
manifest.

### Android release signing

The Release workflow builds Windows and Android for the same `1.0.<build ID>`
version. Android's `versionName` is that version and its integer `versionCode`
is `<build ID> + 1` (maximum 2,100,000,000), greater than the preview's code 1
and increasing with Release workflow run numbers. Re-running an unpublished
release preserves its version; published commits are skipped rather than
overwriting historical assets. Keep the Release workflow/run-number lineage
and package ID `cz.obk.reader` stable. A future versioning change must preserve
increasing version codes.

Generate the permanent release key locally with JDK 21 `keytool`. The helper
defaults to `$HOME\.obk-signing\obk-release.keystore`, refuses repository paths
and existing keystores, and prompts for passwords without displaying them:

```powershell
$env:JAVA_HOME = "$env:LOCALAPPDATA\Microsoft\jdk-21"
.\packaging\new_android_keystore.ps1
```

It creates a 4096-bit RSA key named `obk`, in JKS format so separate key and
store passwords are supported, with a 10,000-day self-signed certificate.
No paid certificate or Play Store account is needed. Keep an offline backup
of the keystore, alias, and both passwords before using it for any release.
Losing the key/passwords prevents compatible updates; do not regenerate or
rotate the key as routine maintenance. Git ignores common keystore extensions
as defense in depth, not permission to store keys in the checkout.

Configure repository **Settings > Secrets and variables > Actions**:

| Secret | Value |
| --- | --- |
| `ANDROID_RELEASE_KEYSTORE_BASE64` | Base64 of the complete keystore file |
| `ANDROID_RELEASE_STORE_PASSWORD` | Keystore password |
| `ANDROID_RELEASE_KEY_ALIAS` | `obk`, unless a different alias was chosen |
| `ANDROID_RELEASE_KEY_PASSWORD` | Private key password |

To upload without printing secret values, sign in to `gh` with repository
secret-management access and run this locally. Enter the same passwords used
when generating the key. Never paste them or the base64 in chat, issues, logs,
screenshots, or source files:

```powershell
$keystore = Join-Path $HOME '.obk-signing\obk-release.keystore'
$alias = 'obk'
$storePassword = Read-Host 'Existing keystore password' -AsSecureString
$keyPassword = Read-Host 'Existing key password' -AsSecureString
[Convert]::ToBase64String([IO.File]::ReadAllBytes($keystore)) |
  gh secret set ANDROID_RELEASE_KEYSTORE_BASE64 --repo iricigor/OBK
if ($LASTEXITCODE -ne 0) { throw 'Cannot upload keystore secret.' }
[Net.NetworkCredential]::new('', $storePassword).Password |
  gh secret set ANDROID_RELEASE_STORE_PASSWORD --repo iricigor/OBK
if ($LASTEXITCODE -ne 0) { throw 'Cannot upload store password secret.' }
$alias | gh secret set ANDROID_RELEASE_KEY_ALIAS --repo iricigor/OBK
if ($LASTEXITCODE -ne 0) { throw 'Cannot upload key alias secret.' }
[Net.NetworkCredential]::new('', $keyPassword).Password |
  gh secret set ANDROID_RELEASE_KEY_PASSWORD --repo iricigor/OBK
if ($LASTEXITCODE -ne 0) { throw 'Cannot upload key password secret.' }
```

The reusable Android release workflow accepts secrets only from the release
caller and restricts production signing to pushes to this repository's `main`.
It rejects missing/empty secrets and invalid base64 before building. The key
is decoded only into a unique runner temporary directory and removed in a
`finally` block. Passwords use the Android SDK's `env:` references, not literal
command-line properties. Do not enable diagnostic MSBuild logging or binary
logs on signing builds; do not upload temporary directories or `bin`/`obj`.
Cancellation may bypass cleanup, so production keys are used only on disposable
GitHub-hosted runners, not persistent self-hosted machines.

`packaging\build_android_release.ps1` publishes in Release configuration with
embedded assemblies and no development signing fallback. It cleans prior
Release outputs before building so a retry cannot reuse partially signed files
or skip signing after an environment-password change. It derives the
expected certificate SHA-256 from the keystore, verifies access to the private
key before compiling, and uses `apksigner` to verify
the APK signature and signer, then `aapt2` to check package ID, version name,
version code, minimum API 29, and absence of the debuggable flag. The signer
parser accepts the certificate digest reported by older numbered-signer output or
build-tools 37's per-signature-scheme output; all reported schemes/signers must
use the same expected certificate. Unknown digest formats are errors.
Only after all checks pass does it produce `dist\OBK-<version>-android.apk` and
`dist\ANDROID-SHA256SUMS.txt`. Its temporary publish directory is removed.
Existing Debug-only fictional verification data and launch handling are
excluded by compilation; signing tests inspect the compiled Release assembly.

The publisher waits for both platform builds, checks both downloaded artifact
checksums, uploads the APK and `ANDROID-SHA256SUMS.txt` alongside the installer
and its unchanged `SHA256SUMS.txt`, then publishes the draft. Missing/invalid
secrets, signing/verification failures, or failed uploads leave the release
unpublished; never bypass a signing failure with a preview APK. Changing secrets
alone does not trigger a release. Configure them before merging this change.

For a local Release build after installing the documented Android dependencies:

```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "$env:LOCALAPPDATA\Microsoft\jdk-21"
try {
  $env:OBK_ANDROID_KEYSTORE_PASSWORD = [Net.NetworkCredential]::new('', (Read-Host 'Keystore password' -AsSecureString)).Password
  $env:OBK_ANDROID_KEY_PASSWORD = [Net.NetworkCredential]::new('', (Read-Host 'Key password' -AsSecureString)).Password
  .\packaging\build_android_release.ps1 -Version 1.0.7 `
    -KeystorePath "$HOME\.obk-signing\obk-release.keystore" -KeyAlias obk
} finally {
  Remove-Item Env:\OBK_ANDROID_KEYSTORE_PASSWORD, Env:\OBK_ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
}
```

Choose an appropriate newer version for an update; example `1.0.7` is not a
version reservation. PR Android checks run `tests\android_release.Tests.ps1`
with a disposable test key, never production secrets. The tests build two
consecutive Release versions and check signatures, versions, checksums, fixture
exclusion, missing passwords, incorrect alias/password, signer mismatch,
version mismatch, and invalid APK rejection. Test APKs are not published.

Before merge, the user must verify a release-signed APK on Android 10+ and
install a newer APK over it, checking account/offline data preservation and
download checksums. Since production releases run only on `main`, stage the
locally built release-signed APKs and checksums in an **unpublished draft
GitHub release** for pre-merge download verification; do not publish an
unreviewed branch or use a disposable test key for this verification. Confirm
the first actual published release's install/update path afterward as well.
Keep the PR draft until setup and verification are complete.

Switching from a differently signed preview requires uninstalling, destroying
local data and its Android Keystore key. Ordinary release updates preserve
them; do not uninstall to update. Android installation is user-confirmed and
requires allowing installation from the download source. The manual in-app
Android updater is described below. Windows SignPath
activation (#38) is independent and retains its existing behavior.

### Manual Android updates

Android Settings exposes the installed display version and **Check for updates**.
Opening Settings or starting the app never checks for releases; a check contacts
GitHub only after the user taps the button. The latest stable release must contain
exactly one `OBK-1.0.<build ID>-android.apk` and `ANDROID-SHA256SUMS.txt`, with
canonical HTTPS download URLs for that release in `iricigor/OBK`. A newer release
with missing or invalid Android assets is reported as a check failure, not as
an up-to-date installation. Older/equal, draft and prerelease versions are not offered.

**Download update** downloads to the app-private `cache/updates` directory using
the shared updater's SHA-256 verification and 500 MiB limit, including streamed
responses without a content length. Partial files are removed after failures or
cancellation. A verified cached APK can be reused, but its checksum is checked
again when retrying. The Android package manager must parse the expected package
identity, display version, increasing version code (`build ID + 1`) and the same
current APK signing certificate(s) as the installed application. Signing-key
rotation is not supported by this updater; a differently signed preview/release
is rejected without suggesting data-destructive uninstallation.

APK archive inspection requests both `GET_SIGNING_CERTIFICATES` and the legacy
`GET_SIGNATURES` flag on all supported Android versions. Some Android parsers
(including Android 10 and the initial Android 13 implementation) collect archive
certificates only with the legacy flag; the same exact signer checks still apply.

The app declares `REQUEST_INSTALL_PACKAGES`. Before installer handoff, it explains
the per-source permission, opens Android's source settings only with consent,
and rechecks permission on return. A non-exported FileProvider grants read-only
access to the APK in `cache/updates`; it does not expose account data. Android's
installer requires the user's confirmation and enforces APK signature verification.
OBK does not report success merely because the installer opened: cancellation or
an unchanged installed version leaves **Install update** available. Updating
normally replaces the running process; the next launch shows the installed version.
If the installer returns without installing the update, Settings offers optional
guidance that Samsung Auto Blocker may be preventing the installation. With consent,
OBK opens Android Security settings, falling back to the main Settings screen and
then written navigation instructions; it does not change security settings or
attempt to diagnose the installer's result.
Checks/downloads can be cancelled, navigating away cancels network work, and
language switching is blocked during an update action.

Before merge, verify on Android 10+ in both themes and languages: manual check,
offline/error recovery, cancelled download, incompatible-preview rejection,
installation-source permission denial, installer cancellation and the Auto Blocker
recovery prompt (including Settings fallback), and successful compatible update
with accounts/offline data preserved. The live latest-release endpoint cannot see
unpublished draft assets. Pre-merge production-key verification
must therefore include manual installation of staged signed APKs; full in-app
update verification between published compatible versions follows publication.
Do not publish an unreviewed branch or replace the production signing key for testing.

### Transferring an account from Windows to Android

`OBK.Core.CredentialTransfer` moves one remembered account offline, with no
network, cloud service, or pairing. On Windows, **Settings > Accounts >
Transfer to Android** (enabled only when the password is saved) shows a QR
code and three words. Android's **Scan from PC** on the sign-in page scans it,
asks for the words, and only prefills the sign-in form; **Remember
credentials** stays unchecked, and nothing is signed in or saved until the user
taps **Sign in and download**.

- The QR text is `OBK1:` plus base64url of salt (16 bytes), nonce (12), tag
  (16), and AES-256-GCM ciphertext of the account JSON (school address,
  username, password, optional child and school names). The prefix is
  authenticated as associated data.
- The key is PBKDF2-HMAC-SHA256 with 600,000 iterations over the normalized
  words (lowercase, single spaces) and a random salt.
- Words are chosen independently with `RandomNumberGenerator` from the
  [EFF large word list](https://www.eff.org/dice) (7,776 words, CC BY 3.0 US),
  embedded unchanged in `src\OBK.Core\Data\eff_large_wordlist.txt`. Three words
  give about 39 bits. Every displayed code uses new words, salt, and nonce.
- The Windows dialog blurs the QR code after 15 seconds by replacing it with a
  12×12-pixel downscaled, blurred copy, so the code data no longer exists in the
  window. The words stay visible until the dialog closes; **Show new code**
  creates a new code and words. Blurring limits screen exposure only: a
  photographed code remains decryptable by anyone who also has the words, and
  offline guessing is limited only by PBKDF2's cost.
  Words not in the list are rejected before the slow key derivation.
- Android declares the `CAMERA` permission (camera hardware optional) but
  requests it only when the user taps **Scan from PC**; denial leaves manual
  sign-in available. Scanning uses ZXing.Net.Maui; Windows renders the QR code
  with QRCoder.

## Data access plan

### Primary: Bakaláři API

Use the school's Bakaláři API to authenticate and download Komens messages and
attachments directly to the device. No OBK backend is planned for the initial
release.

The official mobile application uses this API, but the references below are
community-maintained documentation, not a guarantee of third-party support:

- [API overview](https://github.com/bakalari-api/bakalari-api-v3)
- [Authentication and token renewal](https://github.com/bakalari-api/bakalari-api-v3/blob/master/login.md)
- [Komens messages](https://github.com/bakalari-api/bakalari-api-v3/blob/master/moduly/komens.md)
- [Endpoint list, including attachments](https://github.com/bakalari-api/bakalari-api-v3/blob/master/endpoints.md)

Validate authentication, message retrieval, pagination, and attachment downloads
against the target school's installation before committing to a stack.

### Fallback: web scraping

If the required API operations are unavailable or incompatible, use an embedded
browser where the user signs in to the school's website. Reuse that authenticated
browser session to extract messages and download attachments into the same
offline store used by the API implementation.

Scraping is a fallback, not the default. Explain API compatibility failures and
let the user choose browser sign-in; do not silently switch on network errors or
invalid credentials. Do not bypass authentication or access controls.

The fallback needs a feasibility check on Windows, including login flows,
session persistence, message pagination, and attachment access. Website changes
may require scraper updates.

## Offline storage and sign-in

- Store downloaded messages in a local database and attachments in private local
  files. Clearly distinguish cached messages from attachments not yet downloaded.
- Save the school URL and username in local account settings.
- Prefer retaining a refresh token in OS-backed secure storage rather than
  retaining the password. Ask the user to sign in again when renewal fails.
- The Windows client offers opt-in remembered credentials. It encrypts the
  password in the local database using current-user Windows DPAPI protection.
  API tokens are used in memory only.
- Protect persisted browser sessions as credentials too. Never put credentials
  or tokens in logs, exports, or the repository.
- Decide how to protect cached messages, attachments, and device backups before
  release; credential protection alone does not encrypt the offline archive.

Sync while the application is open initially, with a visible last successful
sync time and explicit errors. Offline reading must remain available when the
network or authentication is unavailable.

For read-only behavior, avoid sending, confirming, hiding, deleting, or marking
messages as read on the server. Verify that API retrieval and browser scraping
do not implicitly trigger these actions. Keep any local reading state separate.

## Implementation phases

1. Verify API compatibility and read-only behavior with a target school account.
   Assess browser scraping if required API capabilities are missing.
2. Choose the Windows implementation stack, considering future desktop and
   mobile support as well as embedded-browser and secure-storage requirements.
3. Build the Windows client: sign-in, message synchronization, attachment
   downloads, offline reading, and clear sync/error status.
4. Add the browser-based fallback, sharing the offline data model with the API
   path. Verify its read-only behavior before enabling it.
5. Extend to macOS, Linux, Android, and iOS after the Windows release.

Keep data retrieval, offline storage, and presentation separable without
building platform-specific implementations prematurely. Each future device
should authenticate independently. Cross-device archive synchronization is not
part of the initial scope, and mobile background synchronization must not be
assumed to be reliable.

## Windows API connection tester

A small Python/Tkinter prototype is available in `tools\api_probe.py`. It does
not select the production application's technology stack. It needs Python 3.10
or later with Tkinter and has no third-party dependencies.

From the repository root, run:

```powershell
python .\tools\api_probe.py
```

Enter the school base URL, username, and password in the local window, then
select **Sign in and test API**. Do not share credentials or tokens in chat.
The tester checks API discovery, uses the community-documented
`client_id=ANDR` password login, and requests the first page of received Komens
messages. It shows only the API version, token lifetime if supplied, and counts
of messages and attachment references. An empty inbox is a successful response.

Credentials, tokens, and message content are not saved or logged. The password
field is cleared when the request starts. Sensitive values exist in process
memory during testing; Python cannot guarantee their immediate erasure.
Requests require valid HTTPS and do not follow redirects.

Opening the school's normal login page in the default browser does not provide
this tester with an API token. Although the target school advertises
authorization-code/PKCE support, a browser callback flow would need an approved
client registration and redirect URL. Such approval is not available for this
project, so the prototype uses direct API login instead. That login may still
be rejected; the tester reports failure rather than bypassing access controls.

The tester does not download attachments, verify all pagination, or establish
that message retrieval has no implicit read-state side effects. It never calls
the message modification endpoints. These remaining checks are required before
building the offline client.

This historical tester remains an optional development tool, not part of the
production app, installer, or validation/release pipelines. Run its mocked
tests without school credentials:

```powershell
python -m unittest discover -s .\tools -p "test_api_probe.py"
```

## Windows desktop client

### Validation optimization

Validation runs .NET tests and installer verification in parallel Windows jobs.
Both cache NuGet packages using the SDK configuration and project manifests as
the cache key inputs; builds still restore dependencies on cache misses.
Release publication waits for both jobs to succeed.

Validation installers use `lzma2/fast` compression, retaining all published-app,
install/start/uninstall, and legacy-directory cleanup checks. Release and local
builds retain the default `lzma2` solid compression. Validation installers may
be larger; they are verification artifacts, not release assets.

The installer build logs application publish and installer compilation times
separately. Compare job timings on cold- and warm-cache CI runs to measure gains;
parallel jobs duplicate some build work and may use more runner minutes.

Run the three-pane application from source with the .NET 10 SDK:

```powershell
dotnet run --project .\src\OBK.App\OBK.App.csproj
```

The installed application does not require any development tools.
On first start (when no account is known), a login popup searches the bundled
school directory by school name,
town, or school-specific URL text. Once fewer than seven schools match, selecting
one confirms the school in a summary card. Users can also enter the school's
website manually. Credentials remain hidden until the school is confirmed.
The popup then asks for username, password, and an optional child name. The
child name is shown in the left column instead of the school name when supplied.
Changing the selected school clears the account-specific credential and child
fields so they cannot be reused unintentionally.
**Remember credentials on this Windows account** is off by default. Each
remembered school and username is saved as its own encrypted profile. Accounts
used without remembering the password are also retained in encrypted account
history, so they can be recognized and selected later. The
initial login and **Add child** flow list previously used accounts;
the list collapses while a school search is active, so up to six matches fit
without scrolling. Choosing one restores its username and optional child name;
passwords are filled only for explicitly remembered profiles. Choosing or
searching for a different school starts with blank account fields.
Later starts open the remembered scope (**All children** or the last used child's
offline archive) without signing in; the
popup appears only from **Add child**, or from **Refresh** when a password is
needed. Signing in happens only when **Refresh** or **Sign in and sync** is used.
The action footer stays visible while the form scrolls, with **Read offline**
separate from **Sign in and sync**.

The Fluent workspace has three columns: child/folder navigation, a virtualized
message list, and a reading pane. The left column lists every known child or
school account; clicking one opens its offline archive for the current school
year without network requests. With two or more children, **All children** comes
first, shown with overlapping avatars of up to three children. It merges every
child's archive for the current school year into one set of folders, like the
Android default scope. The chosen scope is remembered; without a saved choice,
two or more children start in **All children**. **Add child** opens the account picker.

A message is new when it is in **Inbox** or **Noticeboard** and has not been
opened on this PC; **Sent** and **Excuses** are never new. Windows and Android
share this rule and the encrypted per-account `opened_messages` table: the first
load of an account's archive records its existing messages as opened, so only
later arrivals are new. The school's read state is no longer shown and is never
changed. Blue badges on the children and folders count new messages; **All
children** sums its children, and folder badges follow the current scope. Totals
appear in the list caption, for example `7 messages · 3 new · All children`
(searches show the match count instead). New rows show a dot, a semibold subject,
and an accent-colored date. Clicking a message, selecting it with the keyboard,
pressing Enter or Space on the selected message, or opening it from a related
link marks it opened. The automatic selection when a folder opens, the list
refreshes, the scope changes, or search results change does not.

Each list item has two lines: subject and date, then the sender (or recipient in
Sent). In **All children**, rows also show the owning child's avatar, the second
line reads `Child · Sender`, and the reading pane's metadata adds `Child: <name>`.
Attachments, related messages, and related links always use the owning child's
account and archive. Sent excuses, identified by the API message type `OMLUVENKA` (not by subject
text), appear in a separate **Excuses** folder after **Sent** instead of in Sent,
and carry an **Excuse** badge. The search box filters the current folder
live by subject, contents, sender/recipient, and sent date (for example
`3.11.2026` or `2026-11-03`), ignoring case and diacritics; every word must
match. **Sort** orders by date (newest first), sender, or subject; **Group**
groups by date (Today, Yesterday, This week, This month, then months), sender,
or subject, ignoring Re:/Odp:/Fwd: prefixes. **All children** also offers
**Child** grouping; switching to a single child replaces it with no grouping.
Click a group header to collapse or expand it;
groups expand again when the list is re-sorted, searched, or reopened.

Above the message body, **Possibly related** links possible previous and next
messages across Inbox and Sent of the same child, oldest first. Each shows its
day relative to the selected message and the sender (**From: you** for Sent).
For a message received today, the days count back from today: Today, Yesterday,
then `N days ago`. For an older message, they count from its own date:
`N days before`, Same day, or `N days after`. Today and Same day include the
time, for example `Same day 11:40`. Days are local calendar dates;
Czech and Ukrainian use their plural forms. The exact date and time appear in
each link's tooltip and accessible name. When a side has several, they are
numbered outward from the current message, for example Previous (2), Previous (1). Komens lists do not expose reply
links, so this is a heuristic: subjects must share enough words (prefixes,
case, and diacritics are ignored, so an added or changed middle part such as
"Hodnocení / Evaluation" still matches) and the other person must match. Matches
may be wrong or incomplete; selecting one opens it in its folder. When three or more
messages share the subject and person (for example repeated excuses), a link also
needs similar body text, or must be the adjacent message from the other side
(Inbox vs Sent) with no same-subject message from the selected message's side in
between; otherwise nothing is shown rather than a guess.
Selecting a message never marks it read on the school server or sends any request.
Switching folders restores the last selected message in that folder for the
current app session. On a first visit, or if that message is no longer present,
the newest message by sent date is selected and scrolled into view. Empty
folders remain unselected. Selection memory resets when changing children, the
**All children** scope, or school years; it is not saved when the app closes.
Drag the dividers to resize the sidebar, list, and reading pane. Dragging the
sidebar narrower than about 140 pixels, or using the arrow button at its bottom,
switches it to icons only (names in tooltips); the arrow expands it again. The layout
adapts down to an 880-pixel-wide window while retaining all three columns.
The Sent folder is for reading previously sent messages, not composing or
sending new ones.

The sun/moon button switches **Light** and **Dark** immediately, including
message text, login controls, menus, and window chrome. **Settings > Appearance**
offers the same choice; both stay in sync. The choice is saved
per Windows account in `%LOCALAPPDATA%\OBK\appearance.json` and restored on
startup. It contains only the theme, reading size, list density,
**All children** scope choice, and **Always refresh all children** preference,
not credentials or archive data. Older settings files default the new preference
to off.

**Settings**, at the bottom of the sidebar, replaces the message list and
reading pane with Windows 11-style sections that start collapsed:

- **Appearance**: Light or Dark theme; **Reading size** (Small, Medium, Large)
  scaling the message list and reader text; and **Message list density**
  (Compact, Relaxed) for row spacing. Medium and Relaxed match the original look.
- **Refresh**: **Always refresh all children**, off by default. When enabled,
  manual refresh updates every known child even in a single-child view.
- **Accounts**: each known child or school, with **Rename** for the optional
  child name (empty shows the school name), and **Save password**, or
  **Change password** and **Clear password** when one is saved. Saving or
  changing a password signs in to the school once to verify it; nothing is
  saved if sign-in fails. Clearing keeps the account and its offline archive.
  None of these actions change the active child.
- **About**: installed version, update status, **Check now**, and a **GitHub
  repository** link that opens the project in the system browser.

Android **Settings** also includes the **GitHub repository** link below its
app-update controls. Neither platform contacts GitHub for this link until it is
activated.

**Back to messages**, or choosing a folder or child, returns to the messages.
The title bar retains native dragging, resizing, caption controls, and Windows
11 maximize-button snap layouts. The active child is highlighted in the left column. **Add child** opens the previously used account picker directly.
Closing the picker returns to the current school unchanged; the active account
changes only after **Sign in and sync** or **Read offline**.

**Refresh** synchronizes the selected child, or every known account in
**All children**. Enabling **Settings > Refresh > Always refresh all children**
overrides the single-child scope on both platforms; Android scheduled checks
continue to check all children regardless of this preference.
Targeted accounts with remembered passwords synchronize one after another,
then the visible archive reloads. Targeted accounts without a saved password
are listed in the status bar; the sign-in dialog opens for the current
account if it needs a password, or for the first account if none could refresh.
**All children** has no current account, so it opens the dialog only when no
account could refresh. In that scope, the status bar shows the number of
children, the oldest last synchronization, and any children whose archive has
not been downloaded yet.
A failure for one account is reported and does not replace its offline archive
or stop the others. Saved credential
profiles and account history remain available; no offline archive is removed.
Sync progress and errors appear in the header/footer without blocking offline
reading.

The first synchronization fetches every page in all three folders for the
current school year: September 1 through August 31, based on the device's current
date. For example, November 2026 selects 2026/2027. Subsequent syncs retain cached
messages and query each folder from its newest cached sent timestamp, inclusively.
This small overlap catches messages sharing the same timestamp and merges them
by ID rather than duplicating them. Empty folders still query the whole year.

Incremental sync cannot discover backdated messages, older edits, changes to
read/hidden status, or messages removed from the server: there is no documented
change-feed API. Sync filters returned messages to the current school year and
paginates to an empty page even if the server caps page size. Hidden messages
are excluded. Repeated pages and failed requests are reported, not treated as
complete synchronization. Each network request that receives no response within
10 seconds, or fails to connect, is retried once; HTTP errors such as a rejected
sign-in are reported immediately without a retry.

Synchronizations are cached in `%LOCALAPPDATA%\OBK\obk.sqlite3`,
separately for each school URL, username, and school year. Refresh does not
replace the previous archive if any download fails. Initial sign-in saves all
messages before attachments, so a later attachment failure keeps those messages
readable and reports missing files separately. **Read offline** opens the
selected account's existing cache without making network requests.

Sync also downloads every unique attachment referenced by these messages,
across Inbox, Sent, and Noticeboard. A complete cached attachment is reused by
school URL, username, and attachment ID, including during a full message refresh.
Files are streamed into encrypted database chunks; incomplete downloads roll
back instead of appearing available offline. Completed attachment downloads
survive a later sync failure and are reused on retry.

Saved credentials, cached messages, and attachment data/metadata are encrypted with Windows DPAPI,
scoped to the current Windows user. Account indexes (school URL and username),
school name, and sync timestamps are database metadata, not encrypted.
Credentials are never stored as plaintext or logged. Immediate erasure of
sensitive managed process memory is not guaranteed. DPAPI is not protection against
software already running as the same Windows user, and another Windows account
cannot simply copy the database and decrypt it.

Message HTML is rendered as inert, selectable text with basic formatting.
Scripts and embedded resources do not run and external images are not fetched.
Links with absolute `https`, `http`, or `mailto` destinations, plus bare URLs in
the text, are clickable and open in the default browser or mail app; hover to
see the destination. Scheme-less `www.` links are treated as HTTPS rather than
resolved against the school's site. Other destinations (relative,
`javascript:`, `file:`) are shown as plain text.
Attachment cards above the body show filenames, human-readable sizes, and an
offline-cache indicator. Clicking a cached card immediately opens supported
document, image, text, or ZIP files in the default Windows viewer, without an
additional OBK confirmation. Windows downloaded-file protection still applies.
Other file types can be saved, but are not launched directly.

Hover over a card (or focus it with the keyboard) for **Open** and **Save as...**.
Right-click for those actions and **Open folder**, which creates a decrypted
copy and selects it in Explorer. Drag a cached card onto Desktop/Explorer to
copy the file there; decryption runs in the background before the Windows drag
starts, so keep the mouse button held while a large file is prepared.
More than four attachments initially show three cards and a **+N more** toggle.
**Save all** becomes available when every attachment is cached; choose a folder
to export them. Duplicate attachment filenames receive numbered suffixes, and
replacing existing destination files requires confirmation.

Opening creates a decrypted copy in `%LOCALAPPDATA%\OBK\opened` that remains
on disk so external viewers can keep using it. **Save as...** writes a decrypted
file to the location you choose. Opening a folder and dragging also leave source
copies in the local `opened` directory. These copies are outside the encrypted cache;
delete them when no longer needed. Copies receive Windows' Internet-zone
marking to retain downloaded-file security prompts. Only open files you trust,
even if they came from school.

### Existing Python-client archives

The .NET client uses the same database path, table schema, JSON field names,
DPAPI current-user scope, and encrypted attachment chunks as the former Python
client. Existing messages, attachments, remembered credentials, and account
settings are read in place; no destructive migration or redownload is required.
Account URL spellings remain unchanged to preserve archive lookup keys.
Unknown message and attachment fields are retained when saving.
Close the old client before upgrading, and use the same Windows account.
Unreadable or damaged data produces an explicit error, never a plaintext fallback.

The client does not call read-receipt,
confirmation, hide, delete, or send endpoints; server-side effects of list
retrieval still need independent verification.

## Windows installer and releases

Download `OBK-1.0.N-windows-x64-setup.exe` from this repository's
[GitHub Releases](https://github.com/iricigor/OBK/releases). The installer bundles
the self-contained 64-bit .NET/WPF app, SQLite, an inert HTML parser, and
dependency licenses. Windows 10 or later is required; users do not need Python,
Tkinter, or .NET installed separately. Installation is per-user, normally under
`%LOCALAPPDATA%\Programs\OBK`, with a Start-menu shortcut. It does not change
PATH or file associations.

Current installers are **unsigned** while SignPath enrollment is pending.
The [code signing policy](CODE_SIGNING.md) describes opt-in production signing,
signing roles, privacy, and enrollment. Windows or SmartScreen may warn about an
unknown publisher; signing does not guarantee all warnings disappear.
Verify the download's origin and compare its SHA-256 hash with the
release's `SHA256SUMS.txt` before deciding whether to run it:

```powershell
Get-FileHash .\OBK-1.0.0-windows-x64-setup.exe -Algorithm SHA256
```

Reinstalling a newer version upgrades the same application directory and removes
the old bundled Python `runtime` and `app` directories if present.
Uninstalling removes the app and its private runtime but deliberately preserves
`%LOCALAPPDATA%\OBK`, including the encrypted archive and any decrypted opened
copies. Delete that data separately only if you intend to lose it.
Close OBK and external attachment viewers before upgrading or uninstalling.

### Pipeline behavior

Pull requests targeting `main` run **Validation** on Windows: .NET mocked API,
cache-format compatibility, DPAPI and attachment tests,
an installer build, and silent install/start/uninstall verification. The
`windows-installer` workflow artifact contains the installer and its checksum
for manual verification; PR artifacts use placeholder version `1.0.0` and are
not published releases. The same validation supports GitHub's merge queue.

After each push to `main` (including a PR merge), **Release** repeats those
checks and publishes the installer only after they pass. Releases are numbered
`v1.0.<build ID>`, where the build ID is the **Release** workflow's run number
(earlier releases used `v0.1.N`; `1.0` versions supersede them for updates). The tag targets the exact
validated commit, not whichever commit happens to be latest on `main`.
Release runs are serialized with a queue of up to 100 pending runs so multiple
merges do not compete for a version; GitHub does not guarantee dispatch order.
Direct pushes to `main` also trigger releases.

When `SIGNPATH_ENABLED=true`, release builds request manually approved SignPath
signing for OBK-owned binaries, compile the installer from that signed stage,
and request installer signing. Final signatures and installed binaries are
verified before the smoke test and checksum generation. Missing configuration
or signing failures block publication instead of falling back to unsigned
artifacts. PR/local builds remain unsigned. See the
[configuration and verification instructions](CODE_SIGNING.md).

Publication creates a draft release, uploads both assets, and only then makes
it public. Retry a failed run from the Actions UI: a draft for the same commit
is resumed, while an already published release is not duplicated. A failed
build does not create a release. Do not manually repurpose pipeline tags or
change their target commits.

Validation starts automatically; branch protection and mandatory merge checks
are intentionally not configured. Review the
**windows / Tests and installer smoke test** result before merging.
Release validation still prevents a failed build from being published.
Enabling Actions and allowing the release workflow `contents: write` are
necessary. Its version-selection
job needs write permission to see unfinished draft releases for retries;
the publication job uses it to create releases and upload assets.

### Local build and verification

Local Windows builds (Debug or Release, including local installers) show an
orange diagonal **DEV BUILD** ribbon in the top-right corner below the window
controls, with the UTC build time. It overlays the reader and Settings on a
higher z-order without moving or resizing their content. Only the diagonal
strip intercepts clicks; the rest of its transparent corner area remains
click-through. Click it, or focus it with Tab and press Enter/Space, to hide
it until the process exits. It returns after relaunch and uses system colors
in high contrast. Window controls are unchanged.

The desktop project stamps `LocalBuild=true` and the actual UTC `BuildTimestamp`
as assembly metadata, independently of compiler configuration or signing.
CI installer builds explicitly pass `-LocalBuild:$false`; official releases
(signed or unsigned) and CI validation installers have no ribbon.
For a release-style source build, use `-p:LocalBuild=false`. A repeatable build
can override the ISO 8601 timestamp with an explicit offset or `Z`, for example
`-p:BuildTimestamp=2026-11-22T14:00:00Z`. The installer script accepts the same
override as `-BuildTimestamp`. Invalid metadata fails explicitly rather than
displaying a fabricated timestamp. Android and PR/test-specific labels are
outside this feature's scope.

Development runs from source using the .NET command above.
To build an installer, install the .NET 10 SDK and Inno Setup 6
(6.3 or later). From the repository root:

```powershell
dotnet test .\tests\OBK.Tests\OBK.Tests.csproj -c Release
.\packaging\build_installer.ps1 -Version 1.0.0
.\packaging\verify_installer.ps1 -Installer .\dist\OBK-1.0.0-windows-x64-setup.exe
```

For a non-default compiler location, pass `-Iscc "C:\path\to\ISCC.exe"` to the
build script. Build output is under `dist`, which is not committed.
The script uses `dotnet publish -c Release -r win-x64 --self-contained true`.
The published app, README, MIT license, code signing policy, and dependency
license texts are packaged; Python
tools, user data, and repository tests are excluded. NuGet restore and license
downloads need internet access at build time, not for offline reading.

The signing workflow uses `-PrepareOnly` to publish and smoke-test the stage
without compiling an installer, then `-CompilePreparedStage` to compile after
replacing only the three OBK-owned binaries with verified signed copies.
The latter rechecks product/version metadata and smoke-tests the prepared app
without republishing and discarding its signatures. These modes are mutually
exclusive; neither mode is needed for a normal unsigned local build.

The app's `--smoke-test` mode creates and destroys the offline window, checks
SQLite initialization, Windows DPAPI, and synthetic message rendering, then exits.
It uses a temporary database and never opens the real archive or contacts a
school. `--smoke-result` optionally writes a machine-readable result plus PNG
snapshots of the reader in each supported translated language at every reading
size, the corresponding sign-in window, and minimum-size/theme states, for
verification of the installed executable. Errors fail the process rather than
reporting success. Installer verification uses a unique directory and uninstall
registration, without creating shortcuts or changing an existing OBK install.
The smoke test also drives the real sign-in/reader controls with an isolated
HTTP fixture, checking success during foreground message loading, the explicit
1/17 handoff, live 3/17 attachment availability, and completion without resetting
the selected message. Optional snapshots include these light/dark states.

For manual verification, run the installer normally, launch **OBK** from the
Start menu, and confirm the login popup appears. Use **Read offline** without
credentials to check startup, then close the app and uninstall it. No existing
Python or separately installed .NET runtime should be needed.
