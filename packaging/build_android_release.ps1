param(
    [Parameter(Mandatory)]
    [ValidatePattern('^1\.0\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [Parameter(Mandatory)]
    [string]$KeystorePath,
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-zA-Z0-9_-]+$')]
    [string]$KeyAlias,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'),
    [string]$AndroidSdkDirectory = $env:ANDROID_HOME,
    [string]$JavaSdkDirectory = $env:JAVA_HOME
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$versionCode = [long]$Version.Split('.')[2] + 1
if ($versionCode -gt 2100000000) { throw 'Android version code exceeds the supported limit.' }
if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) { throw 'Set ANDROID_HOME or pass -AndroidSdkDirectory.' }
if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) { throw 'Set JAVA_HOME or pass -JavaSdkDirectory.' }
if (-not (Test-Path -LiteralPath $KeystorePath -PathType Leaf)) { throw 'The release keystore was not found.' }
if ([string]::IsNullOrEmpty($env:OBK_ANDROID_KEYSTORE_PASSWORD) -or [string]::IsNullOrEmpty($env:OBK_ANDROID_KEY_PASSWORD)) {
    throw 'Set OBK_ANDROID_KEYSTORE_PASSWORD and OBK_ANDROID_KEY_PASSWORD; no development signing fallback is allowed.'
}
$keytool = Join-Path $JavaSdkDirectory 'bin\keytool.exe'
$temporary = Join-Path ([IO.Path]::GetTempPath()) "obk-android-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $certificate = Join-Path $temporary 'signer.der'
    & $keytool -exportcert -keystore ([IO.Path]::GetFullPath($KeystorePath)) -alias $KeyAlias `
        -storepass:env OBK_ANDROID_KEYSTORE_PASSWORD -file $certificate
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the release certificate; check the keystore, alias, and store password.' }
    $certificateSha256 = (Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash
    & $keytool -certreq -keystore ([IO.Path]::GetFullPath($KeystorePath)) -alias $KeyAlias `
        -storepass:env OBK_ANDROID_KEYSTORE_PASSWORD -keypass:env OBK_ANDROID_KEY_PASSWORD `
        -file (Join-Path $temporary 'key-check.csr')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot unlock the release signing key; check the key password.' }
    # Environment passwords are not incremental inputs; discard failed/stale signing outputs.
    dotnet clean "$root\src\OBK.Android\OBK.Android.csproj" -c Release `
        "-p:AndroidSdkDirectory=$AndroidSdkDirectory" "-p:JavaSdkDirectory=$JavaSdkDirectory" `
        --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot clean previous Android release outputs.' }
    # Passwords are resolved by the Android SDK tasks, not passed on the command line.
    dotnet publish "$root\src\OBK.Android\OBK.Android.csproj" -c Release `
        "-p:AndroidSdkDirectory=$AndroidSdkDirectory" "-p:JavaSdkDirectory=$JavaSdkDirectory" `
        "-p:ApplicationDisplayVersion=$Version" "-p:ApplicationVersion=$versionCode" `
        -p:AndroidPackageFormats=apk -p:AndroidKeyStore=true `
        "-p:AndroidSigningKeyStore=$([IO.Path]::GetFullPath($KeystorePath))" "-p:AndroidSigningKeyAlias=$KeyAlias" `
        -p:AndroidSigningStorePass=env:OBK_ANDROID_KEYSTORE_PASSWORD -p:AndroidSigningKeyPass=env:OBK_ANDROID_KEY_PASSWORD `
        "-p:PublishDir=$temporary\publish\" --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Android release build/signing failed.' }
    $source = Join-Path $temporary 'publish\cz.obk.reader-Signed.apk'
    & "$PSScriptRoot\verify_android_release.ps1" -ApkPath $source -Version $Version `
        -CertificateSha256 $certificateSha256 -AndroidSdkDirectory $AndroidSdkDirectory -JavaSdkDirectory $JavaSdkDirectory
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $name = "OBK-$Version-android.apk"
    $destination = Join-Path $OutputDirectory $name
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name" | Set-Content (Join-Path $OutputDirectory 'ANDROID-SHA256SUMS.txt') -Encoding ascii
    Write-Output "Release APK: $destination"
}
finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
