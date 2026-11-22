param(
    [Parameter(Mandatory)]
    [string]$ApkPath,
    [Parameter(Mandatory)]
    [ValidatePattern('^1\.0\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$CertificateSha256,
    [string]$AndroidSdkDirectory = $env:ANDROID_HOME,
    [string]$JavaSdkDirectory = $env:JAVA_HOME
)

$ErrorActionPreference = 'Stop'
$versionCode = [long]$Version.Split('.')[2] + 1
if ($versionCode -gt 2100000000) { throw 'Android version code exceeds the supported limit.' }
if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) { throw 'The release APK was not produced.' }
if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) { throw 'Set ANDROID_HOME or pass -AndroidSdkDirectory.' }
if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) { throw 'Set JAVA_HOME or pass -JavaSdkDirectory.' }
$tools = Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+$' |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $tools) { throw 'Android SDK build-tools were not found.' }
$apksigner = Join-Path $tools.FullName 'apksigner.bat'
$aapt = Join-Path $tools.FullName 'aapt2.exe'
$previousJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaSdkDirectory
    $signature = & $apksigner verify --verbose --print-certs --min-sdk-version 29 $ApkPath
    if ($LASTEXITCODE -ne 0) { throw 'Android release signature verification failed.' }
    $signers = @($signature | Select-String '^Signer #\d+ certificate SHA-256 digest: ([0-9a-fA-F]{64})$')
    if ($signers.Count -ne 1 -or $signers[0].Matches[0].Groups[1].Value -ine $CertificateSha256) {
        throw 'The APK is not signed with the expected release certificate.'
    }
    $manifest = & $aapt dump badging $ApkPath
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the Android release manifest.' }
    $package = @($manifest | Select-String "^package: name='cz\.obk\.reader' versionCode='$versionCode' versionName='$([regex]::Escape($Version))'(?: |$)")
    if ($package.Count -ne 1) { throw 'Unexpected Android package identity or release version.' }
    if (@($manifest | Select-String "^minSdkVersion:'29'$").Count -ne 1) {
        throw 'The release APK must support Android 10 (API 29).'
    }
    if ($manifest -match '^application-debuggable') { throw 'A debuggable APK cannot be published as a release.' }
    Write-Output "Verified Android release $Version (version code $versionCode), certificate $($CertificateSha256.ToLowerInvariant())."
}
finally {
    $env:JAVA_HOME = $previousJava
}
