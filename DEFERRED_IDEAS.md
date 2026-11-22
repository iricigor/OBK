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

**Status:** Deferred by user decision until Android release signing and APK
publication (#39) are implemented and verified. This is not part of #39.

**Idea:** Check GitHub Releases from OBK, download a newer Android APK, and
hand it to Android's package installer with explicit user consent.

### What is known

- Current Android APKs are development-signed preview artifacts; the release
  workflow currently publishes only the Windows installer.
- #39 establishes persistent release signing, increasing Android version codes,
  and versioned release APKs with checksums. Compatible package identity and
  signing are prerequisites for updates that preserve local data.
- An ordinary sideloaded application cannot silently install updates. Users
  must allow installation from OBK and confirm Android's installer prompt.
- Release signing does not implement update detection, downloads, or installer
  integration. Switching from a preview signed with another key requires
  uninstalling it and loses its local data.

### Why this is deferred

The user chose to implement and verify release distribution first, keeping the
updater as separate work. Published, consistently signed APKs must exist before
the end-to-end update route can be verified.

### Investigation before implementation

- Agree manual versus automatic checks, prompts, and download behavior.
- Design version comparison, HTTPS downloads, APK identity/signature and
  integrity verification, and safe installer handoff on Android 10+.
- Handle denied installation permission, interrupted downloads, offline use,
  rate limits, invalid assets, and cancelled installation explicitly.
- Verify updating between actual releases preserves accounts and offline data.
  Do not assume Windows updater code can be reused unchanged.

### References

- [Android release signing and APK publication](https://github.com/iricigor/OBK/issues/39)
- [Android build and verification](docs/TECHNICAL.md#android-preview)
- [Android package installation permission](https://developer.android.com/reference/android/Manifest.permission#REQUEST_INSTALL_PACKAGES)
