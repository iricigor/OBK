# Marks

Implementation is in progress under #93. Marks are not available in the app yet.

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

- [ ] Typed API and capability responses with malformed-data checks.
- [ ] Encrypted snapshots and local version acknowledgements.
- [ ] Windows Subjects/Recent views and details.
- [ ] Android Subjects/Recent views and details.
- [ ] Focused checks, documentation and maintainer verification.
