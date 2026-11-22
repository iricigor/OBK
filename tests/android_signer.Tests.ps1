$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\android_signing_helpers.ps1')
$expected = 'a' * 64
$unexpected = 'b' * 64

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection: $Message"
}

Assert-AndroidReleaseSigner -CertificateSha256 $expected -SignatureOutput @(
    'Verifies', "Signer #1 certificate SHA-256 digest: $expected"
)
Assert-AndroidReleaseSigner -CertificateSha256 $expected.ToUpperInvariant() -SignatureOutput @(
    'Verifies', "V3.0 Signer: certificate SHA-256 digest: $expected"
)
Assert-AndroidReleaseSigner -CertificateSha256 $expected -SignatureOutput @(
    "V2 Signer #1: certificate SHA-256 digest: $expected",
    "V3.0 Signer: certificate SHA-256 digest: $expected"
)
Assert-Rejected {
    Assert-AndroidReleaseSigner -CertificateSha256 $expected -SignatureOutput @(
        "V3.0 Signer: certificate SHA-256 digest: $unexpected"
    )
} 'expected release certificate'
Assert-Rejected {
    Assert-AndroidReleaseSigner -CertificateSha256 $expected -SignatureOutput @(
        "Signer #1 certificate SHA-256 digest: $expected",
        "Signer #2 certificate SHA-256 digest: $unexpected"
    )
} 'expected release certificate'
Assert-Rejected {
    Assert-AndroidReleaseSigner -CertificateSha256 $expected -SignatureOutput @(
        "V2 Signer #1: certificate SHA-256 digest: $expected",
        "V3.0 Signer: certificate SHA-256 digest: $unexpected"
    )
} 'expected release certificate'
Assert-Rejected {
    Assert-AndroidReleaseSigner -CertificateSha256 $expected -SignatureOutput @('Verifies')
} 'expected release certificate'
Assert-Rejected {
    Assert-AndroidReleaseSigner -CertificateSha256 $expected -SignatureOutput @(
        "Signer #1 certificate SHA-256 digest: $expected",
        "Unknown Signer: certificate SHA-256 digest: $unexpected"
    )
} 'Unrecognized Android signer output'
Write-Output 'Android signer parsing tests passed for build-tools 36/37 and mismatched signers.'
