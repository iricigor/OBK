param(
    [Parameter(Mandatory)]
    [ValidatePattern('^1\.0\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [switch]$SignWindowsRelease,
    [string]$WindowsCertificateThumbprint = $env:SIGNPATH_CERTIFICATE_THUMBPRINT
)

$ErrorActionPreference = 'Stop'
$installer = ".\dist\OBK-$Version-windows-x64-setup.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = "$hash  OBK-$Version-windows-x64-setup.exe"
if ((Get-Content -LiteralPath '.\dist\SHA256SUMS.txt' -Raw).Trim() -cne $expected) {
    throw 'Final installer checksum does not match SHA256SUMS.txt.'
}
$apk = ".\dist\OBK-$Version-android.apk"
$androidHash = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
$androidExpected = "$androidHash  OBK-$Version-android.apk"
if ((Get-Content -LiteralPath '.\dist\ANDROID-SHA256SUMS.txt' -Raw).Trim() -cne $androidExpected) {
    throw 'Final APK checksum does not match ANDROID-SHA256SUMS.txt.'
}
if ($SignWindowsRelease) {
    & "$PSScriptRoot\verify_signatures.ps1" -Files $installer -Version $Version -CertificateThumbprint $WindowsCertificateThumbprint
}
$tag = "v$Version"
gh release view $tag --repo $env:GITHUB_REPOSITORY --json isDraft > $null 2>&1
if ($LASTEXITCODE -ne 0) {
    gh release create $tag --repo $env:GITHUB_REPOSITORY --target $env:GITHUB_SHA --draft --generate-notes --title "OBK $tag"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create draft release.' }
}
else {
    $release = gh release view $tag --repo $env:GITHUB_REPOSITORY --json isDraft | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing release.' }
    if ($release.isDraft -ne $true) { throw 'An existing published release must not be overwritten.' }
}
# Keep the Windows updater's checksum asset unchanged; Android has its own file.
gh release upload $tag $installer '.\dist\SHA256SUMS.txt' $apk '.\dist\ANDROID-SHA256SUMS.txt' --repo $env:GITHUB_REPOSITORY --clobber
if ($LASTEXITCODE -ne 0) { throw 'Cannot upload release assets; release remains draft.' }
gh release edit $tag --repo $env:GITHUB_REPOSITORY --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Cannot publish release.' }
