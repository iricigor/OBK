# Code signing policy

## Enrollment status

SignPath Foundation enrollment is pending. Current releases are unsigned.
Do not describe OBK as a SignPath-supported project until it is accepted.
Signing does not guarantee that Windows SmartScreen will display no warnings.

## Agreed implementation

Issue [#26](https://github.com/iricigor/OBK/issues/26) tracks the Windows-only
integration: MIT licensing for OBK-owned code, preserved dependency notices,
accurate privacy documentation, OBK product/version metadata, and SignPath
signing on GitHub-hosted release runners.

Pull-request and local builds remain unsigned. Production signing will be
explicitly enabled only after enrollment and configuration. Once enabled,
signing or signature-verification failures must stop publication. The final
installer must pass the installation smoke test; SHA-256 checksums must be
generated from the final signed bytes.

Sign only OBK-owned binaries and the installer, not upstream .NET or dependency
binaries. Certificate private keys stay in the signing service. Each production
signing request requires a trusted maintainer's manual approval.

Application instructions, signing roles, privacy details, and exact configuration
will be completed during implementation. End-to-end production signing requires
Foundation acceptance and cannot be demonstrated by an unsigned local build.
