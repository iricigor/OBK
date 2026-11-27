# Deferred ideas

Ideas recorded here are not committed implementation work. Revisit them when
their prerequisites are understood and their scope is agreed.

## DI-01: Komens message hierarchy (original messages and replies)

**Status:** Deferred pending verification of API response metadata.

**Idea:** Group messages into conversations and distinguish original messages
from replies using explicit links supplied by Bakalari, if available.

### What is known

The community API documentation includes `PreviousMessageId` in the payload for
`POST /api/3/komens/message`. This appears to identify the preceding message when
sending a reply, suggesting that Bakalari can maintain a message relationship.
The documented example sets it to `null`; it does not establish the precise
semantics of a populated value.

However, the documented received/sent message lists and individual message
detail responses do not show `PreviousMessageId` or a thread identifier.
`CanAnswer` only indicates whether replying is allowed, not which message is
being answered. These references are community-maintained, not an official
compatibility guarantee.

### Why this is deferred

Support for linking a reply when sending does not prove that a read-only client
can retrieve that link. OBK needs evidence from actual API responses before it
can reliably reconstruct an original/reply hierarchy.

Matching subjects, participants, timestamps, or quoted message text could
suggest related messages, but would be heuristic. Such matches must not be
presented as confirmed parent/reply relationships.

### Investigation before implementation

- Inspect received and sent list/detail responses for a known original/reply
  pair, without sending messages or changing server-side message state.
- Check for `PreviousMessageId` or another explicit relationship field, and
  verify whether it references the immediate parent or the conversation root.
- Check nested replies, missing parents, and links across received/sent folders.
- If reliable metadata exists, agree the offline storage and conversation UI
  scope separately. Otherwise, keep messages independent unless an explicitly
  labeled heuristic grouping approach is agreed.

Do not commit private message payloads or credentials as investigation examples.
Sending messages remains outside OBK's scope.

### References

- [Sending a Komens message: payload containing PreviousMessageId](https://github.com/bakalari-api/bakalari-api-v3/blob/master/moduly/komens_send.md)
- [Komens message lists and details](https://github.com/bakalari-api/bakalari-api-v3/blob/master/moduly/komens.md)

## DI-02: Apple iPhone (iOS) version

**Status:** Deferred. Paid Apple Developer membership is a deal breaker for
the proposed distribution route; the other development and testing hurdles
also make this unsuitable for now.

**Idea:** Port the Android .NET MAUI reader to iOS, retaining OBK's read-only
behavior and encrypted offline archives. This entry concerns iPhone support,
not a separate macOS desktop app.

### What is known

- The repository currently has an Android-only MAUI project targeting
  `net10.0-android`, with shared C# core logic; there is no iOS application.
- MAUI supports iOS, so the core and much of the mobile interface are candidates
  for reuse. The amount of reuse is an expectation, not a verified port.
- Android Keystore-backed data protection and Android startup code cannot be
  used unchanged on iOS. An iOS storage-protection implementation and platform
  configuration are needed.
- iOS builds require Apple's macOS/Xcode toolchain. GitHub Actions offers
  hosted macOS runners, so owning or borrowing a Mac is not essential for
  automated builds.
- TestFlight requires Apple Developer Program membership, normally US$99 per
  year or a local equivalent. It allows invitation-based testing without a
  public App Store release, but still requires uploading builds to Apple.

### Why this is deferred

The membership cost is the primary blocker. Additional hurdles discussed are:

- No local Mac is available for development, simulator testing, or direct
  Xcode installation; a hosted build is not equivalent to local device
  debugging.
- Signing certificates, provisioning profiles, Apple account setup, and
  secure CI credentials add maintenance even with GitHub-hosted builds.
- iPhone installation is not an Android-style unsigned APK download.
  Free personal-team development installation has restrictions and roughly
  seven-day provisioning expiry, making repeated installation impractical.
  Paid ad hoc distribution requires registering devices and maintaining
  valid signing/provisioning.
- TestFlight builds expire after 90 days, and external testing can require
  Apple's beta review. This is not permanent, fully offline distribution.
- Camera/QR scanning, attachment opening and sharing, navigation, encrypted
  storage, and real-device behavior still need iOS-specific verification.

### Investigation before implementation

- Revisit only if an acceptable membership cost or a suitable alternative
  distribution route becomes available; recheck Apple's current terms.
- Agree scope and acceptance criteria before implementation. Do not treat
  shared MAUI code as proof that iOS support is already working.
- Verify .NET/iOS workload and Xcode compatibility on the selected macOS
  runner, runner costs, and the signing/upload process.
- Design iOS Keychain-backed key management and authenticated archive
  encryption, including key loss, reinstall, and backup behavior.
- Plan real-iPhone testing and ongoing build renewal. Verify login, offline
  reading, credential transfer, permissions, and attachment handling without
  changing server-side message state.
- A web/PWA alternative would be a separate proposal, not an approved
  substitute; its offline storage and security limitations need investigation.

### References

- [Android preview and current scope](README.md#android-preview)
- [Current Android target](src/OBK.Android/OBK.Android.csproj)
- [Android-specific data protection](src/OBK.Android/Platforms/Android/AndroidDataProtection.cs)
- [GitHub-hosted macOS runners](https://docs.github.com/en/actions/concepts/runners/github-hosted-runners)
- [Apple Developer membership comparison and personal-team limits](https://developer.apple.com/support/compare-memberships/)
- [Distribution to registered devices](https://help.apple.com/xcode/mac/current/en.lproj/dev7ccaf4d3c.html)
- [TestFlight testing and build expiry](https://developer.apple.com/testflight/)

## DI-03: Android in-app updates from GitHub Releases

**Status:** Implemented in draft PR #56 (tracked by #55): manual checks from Android Settings,
with downloads initiated by the user and user-confirmed installation. No
startup or background checks. User/device verification and merge are pending.

**Idea:** Check GitHub Releases from OBK, download a newer Android APK, and
hand it to Android's package installer with explicit user consent.

### What is known

- #39 established persistent release signing, increasing Android version codes,
  and versioned release APKs with checksums. Release v1.0.27 includes an Android
  APK and `ANDROID-SHA256SUMS.txt`; preview APKs remain development-signed.
  Compatible package identity and
  signing are prerequisites for updates that preserve local data.
- An ordinary sideloaded application cannot silently install updates. Users
  must allow installation from OBK and confirm Android's installer prompt.
- Release signing does not implement update detection, downloads, or installer
  integration. Switching from a preview signed with another key requires
  uninstalling it and loses its local data.

### Why this is deferred

The user chose to implement release distribution first, keeping the updater as
separate work. Signed APK publication is now available, and the user agreed to
manual-only updates. End-to-end updates still require device verification.

### Investigation before implementation

- Manual checks and user-initiated downloads are the agreed scope; automatic
  checks remain outside this implementation.
- Implemented checks cover version comparison, canonical HTTPS assets,
  SHA-256, package/version identity, current signer compatibility and
  user-confirmed installer handoff. This does not support signing-key rotation.
- Android 10 emulator verification covers live release discovery,
  development/release signer mismatch rejection, cancelled-download cleanup,
  and English/light and Czech/dark Settings rendering. Emulator testing found
  and fixed UI-thread HTTP cleanup on cancellation.
- Device verification of installation-source permission, installer
  cancellation and successful compatible release installation is still pending.
- Verify updating between actual releases preserves accounts and offline data.
  Do not assume Windows updater code can be reused unchanged.

### References

- [Android release signing and APK publication](https://github.com/iricigor/OBK/issues/39)
- [Android build and verification](docs/TECHNICAL.md#android-preview)
- [Android package installation permission](https://developer.android.com/reference/android/Manifest.permission#REQUEST_INSTALL_PACKAGES)

## DI-13: Subject grade gauges and weight-scaled assessment bars

**Status:** Deferred follow-up tracked in #97. Not started; recording this idea
does not authorize implementation or expand marks PR #94.

**Idea:** For a confirmed 1-to-5 grading scale, with 1 best and 5 worst, show an
average gauge when a subject is selected: green 1.00 at the left and red 5.00
at the right, with localized decimal separators. Add vertical assessment bars
whose colors represent grades and whose heights represent their weights.

### What is known

- The marks model preserves original grade text, nullable weights, points-based
  grading and school-supplied subject averages.
- These fields do not establish a universal numeric conversion or grading
  scale. Observing only grades from 1 to 5 is not proof of the full scale.
- Values for modified grades such as `1-` might be inferred if all contributing
  assessments, weights and averaging rules are known. This is an unverified
  hypothesis, not evidence that `1-` means 1.5.
- Rounded averages may constrain only a range. Missing or excluded assessments,
  multiple unknown grade symbols and school-specific rules can make inference
  ambiguous or impossible.

### Why this is deferred

The native marks foundation and its user verification remain separate work.
Eligibility, chart placement, weight scaling and modified-grade interpretation
need investigation and scope agreement before numeric visualizations are safe.

### Investigation before implementation

- Define scale eligibility and whether it applies per subject or account.
- Agree the gauge's average source and bar placement/order/weight scaling.
- Handle points, mixed/textual grades, modifiers, missing/zero weights and
  invalid averages without inventing values or hiding the original text.
- Investigate modified-grade inference using known weighted assessments and
  rounded averages; distinguish estimates from authoritative API values and
  reject ambiguous conversions.
- Preserve Windows/Android density, reading-size settings, offline behavior
  and account isolation. Provide grade/weight labels, not color alone.
- Agree scope, use fictional fixtures and obtain user verification before any
  separately approved merge. Do not publish private school data or credentials.

### References

- [Follow-up tasks and acceptance criteria](https://github.com/iricigor/OBK/issues/97)
- [Marks foundation](https://github.com/iricigor/OBK/issues/93)
- [Native marks implementation](https://github.com/iricigor/OBK/pull/94)
- [Marks data model](src/OBK.Core/Marks.cs)
- [Current marks behavior](docs/features/MARKS.md)
