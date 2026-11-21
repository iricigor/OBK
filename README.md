# OBK
Offline Bakaláři Komens

## Purpose and scope

OBK is a read-only Bakaláři Komens client that downloads messages for offline
reading. Sending messages is out of scope.

The first release targets Windows desktop, building on the Python/Tkinter API
tester with SQLite storage. Later releases are planned for macOS, Linux,
Android, and iOS; their implementation stack is not yet decided.

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

Run its local, mocked tests without school credentials:

```powershell
python -m unittest discover -s .\tools -p "test_*.py"
```

## Windows desktop client

Run the three-pane application from the repository root:

```powershell
python .\tools\obk_app.py
```

It uses Python 3.10 or later with Tkinter and has no third-party dependencies.
At startup, a login popup asks for the school HTTPS base URL, username, and
password. **Remember credentials on this Windows account** is off by default.
When enabled, the next login popup is prefilled; it does not sign in silently.
Turning the option off or selecting **Forget credentials** removes the saved
credentials without deleting offline messages.

The left tree uses the school name returned by the API, with **Inbox**, **Sent**,
and **Noticeboard** underneath. The upper-right pane lists messages; selecting a
message shows its details and content in the lower-right pane. Both dividers
can be resized. The Sent folder is for reading previously sent messages, not
composing or sending new ones.

The first synchronization fetches every page in all three folders for the
current school year: September 1 through August 31, based on the device's current
date. For example, November 2026 selects 2026/2027. Subsequent syncs retain cached
messages and query each folder from its newest cached sent timestamp, inclusively.
This small overlap catches messages sharing the same timestamp and merges them
by ID rather than duplicating them. Empty folders still query the whole year.

**Full school-year refresh** in the login popup fetches the complete year again.
Use it to discover backdated messages, older edits, changes to read/hidden
status, and messages removed from the server: there is no documented change-feed
API, so incremental date filtering cannot discover all such changes. Both modes
filter returned messages to the current school year and paginate to an empty
page even if the server caps page size. Hidden messages are excluded. Repeated
pages and failed requests are reported, not treated as complete synchronization.

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
Credentials are never stored as plaintext or logged. Python cannot guarantee
immediate erasure of sensitive process memory. DPAPI is not protection against
software already running as the same Windows user, and another Windows account
cannot simply copy the database and decrypt it.

Message HTML is rendered as inert text with basic formatting. Scripts and
embedded resources do not run, external images are not fetched, and link
destinations are displayed as text. Select an attachment in the reading pane
to see its offline availability and size. **Open** launches the default Windows
viewer for supported document, image, text, and ZIP file types after confirmation.
Other file types can be saved using **Save a copy**, but are not launched directly.

Opening creates a decrypted copy in `%LOCALAPPDATA%\OBK\opened` that remains
on disk so external viewers can keep using it. **Save a copy** writes a decrypted
file to the location you choose. These copies are outside the encrypted cache;
delete them when no longer needed. Copies receive Windows' Internet-zone
marking to retain downloaded-file security prompts. Only open files you trust,
even if they came from school.

The client does not call read-receipt,
confirmation, hide, delete, or send endpoints; server-side effects of list
retrieval still need independent verification.

## Windows installer and releases

Download `OBK-0.1.N-windows-x64-setup.exe` from this repository's
[GitHub Releases](https://github.com/iricigor/OBK/releases). The installer bundles
a private 64-bit CPython 3.14 runtime, Tkinter, and the Python runtime licenses.
It does not use PyInstaller. Windows 10 or later is required; users do not need
Python installed. Installation is per-user, normally under
`%LOCALAPPDATA%\Programs\OBK`, with a Start-menu shortcut. It does not change
system Python, PATH, or Python file associations.

Installers are **unsigned**: Windows or SmartScreen may warn about an unknown
publisher. Verify the download's origin and compare its SHA-256 hash with the
release's `SHA256SUMS.txt` before deciding whether to run it:

```powershell
Get-FileHash .\OBK-0.1.0-windows-x64-setup.exe -Algorithm SHA256
```

Reinstalling a newer version upgrades the same application directory.
Uninstalling removes the app and its private runtime but deliberately preserves
`%LOCALAPPDATA%\OBK`, including the encrypted archive and any decrypted opened
copies. Delete that data separately only if you intend to lose it.
Close OBK and external attachment viewers before upgrading or uninstalling.

### Pipeline behavior

Pull requests targeting `main` run **Validation** on Windows: all mocked tests,
an installer build, and silent install/start/uninstall verification. The
`windows-installer` workflow artifact contains the installer and its checksum
for manual verification; PR artifacts use placeholder version `0.1.0` and are
not published releases. The same validation supports GitHub's merge queue.

After each push to `main` (including a PR merge), **Release** repeats those
checks and publishes the installer only after they pass. Numbered releases
start at `v0.1.0`, then increment the patch number. The tag targets the exact
validated commit, not whichever commit happens to be latest on `main`.
Release runs are serialized with a queue of up to 100 pending runs so multiple
merges do not compete for a version; GitHub does not guarantee dispatch order.
Direct pushes to `main` also trigger releases; prohibit them through branch
protection when available.

Publication creates a draft release, uploads both assets, and only then makes
it public. Retry a failed run from the Actions UI: a draft for the same commit
is resumed, while an already published release is not duplicated. A failed
build does not create a release. Do not manually repurpose pipeline tags or
change their target commits.

To enforce pre-merge validation, configure a `main` branch protection rule or
ruleset requiring pull requests and the check
**windows / Tests and installer smoke test**, with checks required against
the current base branch. GitHub currently blocks branch protection for this
private repository without GitHub Pro; the workflow reports results but cannot
prevent merges until that limitation is resolved. Enabling Actions and granting
the release workflow `contents: write` are also necessary. Its version-selection
job needs write permission to see unfinished draft releases for retries;
the publication job uses it to create releases and upload assets.

### Local build and verification

Development still runs directly from source using the commands above.
To build an installer, install 64-bit CPython 3.14 with Tcl/Tk and Inno Setup 6
(6.3 or later). From the repository root:

```powershell
python -m unittest discover -s .\tools -p "test_*.py"
python .\packaging\build_installer.py --version 0.1.0
.\packaging\verify_installer.ps1 -Installer .\dist\OBK-0.1.0-windows-x64-setup.exe
```

For a non-default compiler location, pass `--iscc "C:\path\to\ISCC.exe"` to the
build script. Build output is under `dist`, which is not committed. Only the
three application modules, README, and selected runtime files are packaged;
user data, third-party site-packages, and repository tests are excluded.

The app's `--smoke-test` mode creates and destroys the offline window, checks
SQLite initialization and Windows DPAPI encryption/decryption, and then exits.
It uses a temporary database and never opens the real archive, shows login, or
contacts a school. `--smoke-result` optionally writes a machine-readable result
for verification of the windowed runtime. Errors fail the process rather than
reporting success. Installer verification uses a unique directory and uninstall
registration, without creating shortcuts or changing an existing OBK install.

For manual verification, run the installer normally, launch **OBK** from the
Start menu, and confirm the login popup appears. Use **Read offline** without
credentials to check startup, then close the app and uninstall it. No existing
Python installation should be needed.
