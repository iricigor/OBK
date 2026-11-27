# Marks

Implemented on the draft branch tracked by #93. Windows and Android have native
marks screens; maintainer verification against a real school is still required.

## Agreed behavior

Windows and Android offer **Subjects** and **Recent marks** views, scoped
to one child or all children, with search and assessment details. The feature
preserves OBK's existing message navigation and light/dark appearance.

Marks are downloaded read-only when the account has the `ShowMarks` right,
then stored locally with the same encryption as other archived data. A failed
refresh must preserve the last successful snapshot and report the problem.

Subject averages and textual grades come from the school API. Points-based
assessments remain distinct, and a missing weight is not replaced with an
invented value. Teacher IDs are not presented as verified teacher names.

New or changed assessments are detected locally after an initial baseline.
Opening assessment details acknowledges that version locally, without writing
confirmation or read state to the school.

## Limits

The documented marks endpoint has no historical school-year or semester
selector. A snapshot represents the period exposed by the school server, not
complete assessment history. Report cards, prediction, server confirmation and
Komens evaluation classification are outside this implementation.

## Implementation tasks

- [x] Typed API and capability responses with malformed-data checks.
- [x] Encrypted snapshots and local version acknowledgements.
- [x] Windows Subjects/Recent views and details.
- [x] Android Subjects/Recent views and details.
- [x] Focused checks and documentation.
- [ ] Maintainer verification on both platforms and a real school response.

## Using marks

Choose **Marks** in Windows navigation or the Android drawer. **Subjects**
shows the school's averages; **Recent marks** lists individual assessments,
newest first. Choose one child or **All children** using the existing account
controls (or the child button on Android's marks screen). Search matches
subjects, children, captions, topics and grade/points text.

Both lists use message-style two-line rows, alternating backgrounds and local
new-state dots. Child avatars appear only when showing multiple children.
The existing **Compact / Relaxed** list-density and reading-size settings apply
to marks immediately, just as they do to messages. Full assessment metadata
and individual subject grades remain in details rather than adding list rows.

Opening a subject does not acknowledge its assessments. Explicitly open an
assessment to clear its local **New or changed** indicator and inspect its
dates, weight or points status, teacher ID, topic and assessment type.
Subject notes, provisional grades and the snapshot time appear in details.

Use **Refresh** within Marks to download marks. The existing message refresh,
initial sign-in download and scheduled checks still download Komens only.
The **Always refresh all children** preference also applies to marks.
An account without a remembered password prompts for one for this refresh
only; it is not saved. Cancelling or a download failure is reported and does
not erase a previous snapshot. Other children can still refresh successfully.

Existing snapshots remain available offline. The message school-year selector
does not select a historical marks period: marks always show the latest
successfully saved period exposed by the school.

## Shared implementation

`KomensClient.FetchMarksAsync` first checks the account's `ShowMarks` right,
then retrieves and validates the marks response. `RefreshMarksAsync` saves
only a successful, validated download. Missing or denied capabilities are
reported as errors; no empty snapshot is substituted.

`LocalStore.LoadMarks` reads the latest account-scoped snapshot.
`SaveMarks` stores both marks and acknowledgement versions in an encrypted
SQLite payload, atomically retaining any previous acknowledgements for
assessments still present. The initial download is a baseline, even if empty.
Subsequent arrivals and edits are new until their current version is opened.
Changes to server unread/confirmation flags alone do not count as edits.

`AcknowledgeMark` requires the exact version the user opened. If a concurrent
refresh changed or removed it, acknowledgement fails visibly rather than
marking unseen changes as opened. None of these operations writes to Bakaláři.

The optional semester information is the server's reported current semester,
not a user-selectable historical period. Unknown API fields are retained in
the local payload, but only known assessment content participates in local
change detection. Private payloads are not used in regression fixtures.
