# Marks

Implementation is in progress under #93. The shared API/storage foundation is
implemented, but marks are not available in either app's UI yet.

## Agreed behavior

Windows and Android will offer **Subjects** and **Recent marks** views, scoped
to one child or all children, with search and assessment details. The feature
will preserve OBK's existing message navigation and light/dark appearance.

Marks will be downloaded read-only when the account has the `ShowMarks` right,
then stored locally with the same encryption as other archived data. A failed
refresh must preserve the last successful snapshot and report the problem.

Subject averages and textual grades come from the school API. Points-based
assessments remain distinct, and a missing weight is not replaced with an
invented value. Teacher IDs are not presented as verified teacher names.

New or changed assessments will be detected locally after an initial baseline.
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
- [ ] Windows Subjects/Recent views and details.
- [ ] Android Subjects/Recent views and details.
- [ ] Focused checks, documentation and maintainer verification.

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
