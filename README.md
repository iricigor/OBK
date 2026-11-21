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
