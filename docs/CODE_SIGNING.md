# Code signing policy

## Enrollment status

SignPath Foundation enrollment is pending. Current releases are unsigned.
Do not describe OBK as a SignPath-supported project until it is accepted.
Signing does not guarantee that Windows SmartScreen will display no warnings.

After acceptance, publish the following attribution on the home page and link
this policy from release/download pages:

> Free code signing provided by [SignPath.io](https://about.signpath.io),
> certificate by [SignPath Foundation](https://signpath.org).

The intended certificate publisher is SignPath Foundation, not a personal
certificate issued to the maintainer.

## Responsibilities and approval

- Author and reviewer: [@iricigor](https://github.com/iricigor).
- Release signing approver: [@iricigor](https://github.com/iricigor).

Contributions from other authors require maintainer review, including changes
to workflows, packaging, and dependencies. All signing team members must enable
MFA on GitHub and SignPath. GitHub MFA is already enabled by the maintainer;
SignPath MFA and role assignment must be completed during enrollment.

Configure the production SignPath policy to require manual approval and allow
only the repository's GitHub-hosted release builds from `main`. The workflow
makes two signing requests per release: first for OBK-owned application binaries,
then for the installer containing those signed binaries. Approve both requests
after checking their source commit, version, build results, and artifact scope.
Requests wait up to 30 minutes each; timeout or rejection fails the build.

## Privacy policy

OBK is an unofficial, read-only client for the school's Bakalari service.
School login, refresh, and attachment retrieval send credentials/tokens and
requests directly to the school server selected by the user. API access remains
subject to the service's applicable terms, independently of OBK's MIT license.

The school search directory ships with the application; normal searches do
not query a central directory service. OBK automatically contacts GitHub to
check for releases and downloads release assets when an update is requested.
GitHub and its download infrastructure receive normal network request data,
such as the IP address and request headers, not school credentials or messages.
See [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

OBK has no cloud message storage, analytics, or advertising service. School
credentials are remembered only at the user's request. Credentials, messages,
and attachments are encrypted locally with Windows DPAPI. Account index
metadata is not encrypted, and deliberately opened/exported attachment copies
are decrypted files. Links and opened attachments use external browser/viewer
applications; those applications and destination services have their own
privacy behavior. The selected school's privacy policy also applies to its
service; it varies by school.

Do not replace this description with the blanket claim that OBK makes no
network requests unless specifically requested: automatic update checks exist.

## Application checklist

Apply at [signpath.org/apply](https://signpath.org/apply) after the license and
policy are published on the default branch. Use these answers for the
corresponding fields; the embedded form's labels may change.

| Requested information | Answer |
| --- | --- |
| Project name | OBK - Offline Bakalari Komens |
| Website / source repository | `https://github.com/iricigor/OBK` |
| Download / release page | `https://github.com/iricigor/OBK/releases/latest` |
| License | MIT for OBK-owned code; dependencies retain their own OSS licenses/notices |
| License reference | `https://github.com/iricigor/OBK/blob/main/LICENSE` (after merge) |
| Code signing policy | `https://github.com/iricigor/OBK/blob/main/docs/CODE_SIGNING.md` (after merge) |
| Platform / artifacts | Windows x64; OBK.exe, OBK.dll, OBK.Core.dll and Inno Setup EXE installer |
| Build system | .NET 10 / WPF, Inno Setup 6, GitHub Actions on GitHub-hosted Windows runners |
| Maintainer / contact | Your own name and email; repository owner `iricigor` |

Suggested project description:

> OBK is an open-source, unofficial Windows desktop client for Bakalari Komens.
> It downloads school messages and attachments for offline reading, search,
> and organization. It is read-only: it does not send messages or confirmations
> or mark messages as read. School credentials and downloaded content are
> encrypted locally for the Windows user. Windows x64 installers are built
> from the public repository using GitHub-hosted Actions runners and published
> through GitHub Releases.

If asked why signing is needed, explain that users currently see an unknown
publisher and that signing will authenticate the project's release artifacts.
Do not promise that signing eliminates all SmartScreen warnings.

Use your real maintainer contact details in the application, not in the
repository. Describe adoption, download counts, contributors, and reputation
truthfully if asked; public source and an OSS license do not guarantee approval.
Read the [Foundation conditions](https://signpath.org/terms.html), especially
privacy, review, ownership, and manual-approval requirements, before accepting.
Do not claim enrollment is complete or that production signing is tested.

## Configuration after acceptance

Install the [SignPath GitHub App](https://github.com/apps/signpath) for this
repository and link the SignPath project to its GitHub trusted build system.
Create two artifact configurations from `packaging\signpath-application.xml`
and `packaging\signpath-installer.xml`; validate them in SignPath's editor
against its current schema. Both require the workflow's `version` parameter
and enforce product name `OBK` and the release product version.

The application configuration signs exactly `OBK.exe`, `OBK.dll`, and
`OBK.Core.dll`. Never broaden this to all DLLs: bundled .NET and upstream
libraries are not OBK-owned code. The installer is signed as a PE file after
assembly; its embedded OBK binaries are already signed. The Inno-generated
uninstaller is not signed by this integration. Confirm this scope with
SignPath during enrollment.

In GitHub **Settings > Secrets and variables > Actions**, configure:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | Token with submission permissions for the approved project/policy |
| Variable | `SIGNPATH_ORGANIZATION_ID` | Organization ID assigned by SignPath |
| Variable | `SIGNPATH_PROJECT_SLUG` | Approved SignPath project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Production policy requiring manual approval |
| Variable | `SIGNPATH_APPLICATION_CONFIGURATION_SLUG` | Application artifact configuration slug |
| Variable | `SIGNPATH_INSTALLER_CONFIGURATION_SLUG` | Installer artifact configuration slug |
| Variable | `SIGNPATH_CERTIFICATE_THUMBPRINT` | Approved production certificate's thumbprint, exactly 40 hexadecimal digits |
| Variable | `SIGNPATH_ENABLED` | `true` only when enrollment, policies, and configuration are complete |

The certificate thumbprint is public configuration, not a private key. Obtain
it from the approved certificate, not from an unverified downloaded executable.
Update it deliberately when SignPath rotates the certificate; otherwise
verification blocks publication. Keep tokens and private contact information
out of source, issue bodies, logs, and screenshots.

Unset or `false` `SIGNPATH_ENABLED` preserves unsigned releases and existing
local/PR validation. Any other value is an error. Once `true`, missing settings,
signing failures, unexpected certificate/product/version, missing timestamps,
or invalid signatures stop publication; there is no unsigned fallback.
Pull-request workflows never receive the signing token or request signing.
The certificate's private key remains in SignPath's HSM.

## Release verification

The release build verifies signed application binaries before compiling the
installer, verifies the signed installer, then installs it in an isolated
directory and verifies the installed OBK signatures before running the
existing smoke test and uninstall check. Verification requires a valid embedded
Authenticode signature, the approved certificate thumbprint, timestamp, and
exact OBK product/version metadata.

Only then is `SHA256SUMS.txt` generated from the final installer. Publication
rechecks the downloaded artifact's hash and, when enabled, installer signature.
The existing updater continues to verify SHA-256 against the release checksum;
its behavior is unchanged. A checksum is not a replacement for a signature,
and the updater does not independently enforce a signing certificate.

For a signed release, from the repository root:

```powershell
.\packaging\verify_signatures.ps1 -Files .\dist\OBK-1.0.0-windows-x64-setup.exe `
    -Version 1.0.0 -CertificateThumbprint '<approved 40-digit thumbprint>'
```

Use the actual downloaded version and the independently obtained certificate
thumbprint. Windows Explorer's file properties also show **Digital Signatures**
when present. Older releases and local/PR artifacts remain unsigned.
End-to-end production verification is pending until Foundation enrollment.

After the first successfully verified signed release, update the enrollment
status here, the README attribution, and release notes. Keep the PR draft until
the maintainer's agreed verification is complete.
