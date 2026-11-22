# OBK technical documentation

Developer, maintainer, and security details for OBK (Offline Bakaláři Komens).
For an overview and installation, see the [README](../README.md). Potential
future work and its prerequisites are recorded in [Deferred ideas](../DEFERRED_IDEAS.md).

## Documentation screenshot

`docs\images\obk-main.png` is rendered from fictional sample data, without
contacting any school. Regenerate it after a Release build:

```powershell
.\src\OBK.App\bin\Release\net10.0-windows\OBK.exe --screenshot "$PWD\docs\images\obk-main.png"
```

The command uses a temporary archive that is deleted afterwards.

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

OBK checks the latest stable [GitHub release](https://github.com/iricigor/OBK/releases)
one minute after an account is opened, then every five minutes. The status bar shows
the installed version when current. A newer installer is downloaded in the
background and verified against the release's `SHA256SUMS.txt`; a failed
check or download shows a message and is retried at the next interval. Once ready,
**Update and restart** installs it silently and reopens OBK. Its dropdown offers
**Ignore during this session** and **Install when I exit**. Clicking **You're on the
latest version** (or a check error) checks again immediately. There is no setting to
disable checks yet; only HTTPS assets from this repository's releases are used.

## Application icon

`src\OBK.App\Assets\OBK.ico` (a graduation cap over an envelope) is used for the
executable, windows, and installer. Regenerate it with Pillow after editing
`tools\make_app_icon.py`: `python .\tools\make_app_icon.py`.

## Purpose and scope

OBK is a read-only Bakaláři Komens client that downloads messages for offline
reading. Sending messages is out of scope.

The Windows client uses C#/.NET 10 LTS with WPF and SQLite storage, published
self-contained. Users need neither Python/Tkinter nor a separately installed
.NET runtime. WPF is Windows-only; planned macOS, Linux, Android, and iOS
releases will need a separate presentation implementation.

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
Later starts open the last used child's offline archive without signing in; the
popup appears only from **Add child**, or from **Refresh** when a password is
needed. Signing in happens only when **Refresh** or **Sign in and sync** is used.
The action footer stays visible while the form scrolls, with **Read offline**
separate from **Sign in and sync**.

The Fluent workspace has three columns: child/folder navigation, a virtualized
message list, and a reading pane. The left column lists every known child or
school account; clicking one opens its offline archive for the current school
year without network requests. **Add child** opens the account picker.
**Noticeboard**, **Inbox**, **Sent**, and **Excuses** show actual archive counts; unread dots
reflect the cached server read state.

Each list item has two lines: subject and date, then the sender (or recipient in
Sent). Sent excuses, identified by the API message type `OMLUVENKA` (not by subject
text), appear in a separate **Excuses** folder after **Sent** instead of in Sent,
and carry an **Excuse** badge. The search box filters the current folder
live by subject, contents, sender/recipient, and sent date (for example
`3.11.2026` or `2026-11-03`), ignoring case and diacritics; every word must
match. **Sort** orders by date (newest first), sender, or subject; **Group**
groups by date (Today, Yesterday, This week, This month, then months), sender,
or subject, ignoring Re:/Odp:/Fwd: prefixes. Click a group header to collapse or expand it;
groups expand again when the list is re-sorted, searched, or reopened.

Above the message body, **Possibly related** links possible previous and next
messages across Inbox and Sent, oldest first, each showing its date (for example
21.Sep 15:53) and sender (**From: you** for Sent). When a side has several, they are
numbered outward from the current message, for example Previous (2), Previous (1). Komens lists do not expose reply
links, so this is a heuristic: subjects must share enough words (prefixes,
case, and diacritics are ignored, so an added or changed middle part such as
"Hodnocení / Evaluation" still matches) and the other person must match. Matches
may be wrong or incomplete; selecting one opens it in its folder. When three or more
messages share the subject and person (for example repeated excuses), a link also
needs similar body text, or must be the adjacent message from the other side
(Inbox vs Sent) with no same-subject message from the selected message's side in
between; otherwise nothing is shown rather than a guess.
Selecting a message does not mark it read or send any server requests.
Switching folders restores the last selected message in that folder for the
current app session. On a first visit, or if that message is no longer present,
the newest message by sent date is selected and scrolled into view. Empty
folders remain unselected. Selection memory resets when changing accounts or
school years; it is not saved when the app closes.
Drag the dividers to resize the sidebar, list, and reading pane. Dragging the
sidebar narrower than about 140 pixels, or using the arrow button at its bottom,
switches it to icons only (names in tooltips); the arrow expands it again. The layout
adapts down to an 880-pixel-wide window while retaining all three columns.
The Sent folder is for reading previously sent messages, not composing or
sending new ones.

The sun/moon button switches **Light** and **Dark** immediately, including
message text, login controls, menus, and window chrome. The choice is saved
per Windows account in `%LOCALAPPDATA%\OBK\appearance.json` and restored on
startup. It contains only the theme, not credentials or archive data.
The title bar retains native dragging, resizing, caption controls, and Windows
11 maximize-button snap layouts. The active child is highlighted in the left column. **Add child** opens the previously used account picker directly.
Closing the picker returns to the current school unchanged; the active account
changes only after **Sign in and sync** or **Read offline**.

**Refresh** synchronizes every known account that has a remembered password,
one after another, then reloads the visible archive. Accounts without a saved
password are listed in the status bar; the sign-in dialog opens for the current
account if it needs a password, or for the first account if none could refresh.
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

Complete synchronizations are cached in `%LOCALAPPDATA%\OBK\obk.sqlite3`,
separately for each school URL, username, and school year. An interrupted or
failed sync does not replace the previous archive. **Read offline** opens the
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

Installers are **unsigned**: Windows or SmartScreen may warn about an unknown
publisher. Verify the download's origin and compare its SHA-256 hash with the
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
The published app, README, and dependency license texts are packaged; Python
tools, user data, and repository tests are excluded. NuGet restore and license
downloads need internet access at build time, not for offline reading.

The app's `--smoke-test` mode creates and destroys the offline window, checks
SQLite initialization, Windows DPAPI, and synthetic message rendering, then exits.
It uses a temporary database and never opens the real archive or contacts a school.
`--smoke-result` optionally writes a machine-readable result plus PNG snapshots
of the reader at normal/minimum sizes and a synthetic login window, for verification
of the installed executable. Errors fail the process rather than
reporting success. Installer verification uses a unique directory and uninstall
registration, without creating shortcuts or changing an existing OBK install.

For manual verification, run the installer normally, launch **OBK** from the
Start menu, and confirm the login popup appears. Use **Read offline** without
credentials to check startup, then close the app and uninstall it. No existing
Python or separately installed .NET runtime should be needed.
