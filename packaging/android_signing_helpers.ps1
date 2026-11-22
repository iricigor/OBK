function Assert-AndroidReleaseSigner {
    param(
        [Parameter(Mandatory)]
        [string[]]$SignatureOutput,
        [Parameter(Mandatory)]
        [string]$CertificateSha256
    )

    $lines = @($SignatureOutput | Where-Object { $_ -match 'certificate SHA-256 digest:' })
    $matches = @($lines | Select-String '^(?:Signer #\d+|V[1-4](?:\.\d+)? Signer(?: #\d+)?):? certificate SHA-256 digest: ([0-9a-fA-F]{64})$')
    if ($lines.Count -ne $matches.Count) {
        throw "Unrecognized Android signer output: $($lines -join '; ')"
    }
    $digests = @($matches | ForEach-Object { $_.Matches[0].Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)
    if ($digests.Count -ne 1 -or $digests[0] -ine $CertificateSha256) {
        throw "The APK is not signed with the expected release certificate. Expected SHA-256: $CertificateSha256. Reported digests: $($digests -join '; ')"
    }
}
