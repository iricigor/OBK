param(
    [string]$DestinationPath = (Join-Path $HOME '.obk-signing\obk-release.keystore'),
    [ValidatePattern('^[a-zA-Z0-9_-]+$')]
    [string]$KeyAlias = 'obk',
    [string]$JavaSdkDirectory = $env:JAVA_HOME
)

$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($DestinationPath)
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ($destination.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep the release keystore outside the repository.'
}
if (Test-Path -LiteralPath $destination) { throw 'The keystore already exists; it will not be replaced.' }
if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) { throw 'Set JAVA_HOME or pass -JavaSdkDirectory.' }
$keytool = Join-Path $JavaSdkDirectory 'bin\keytool.exe'
if (-not (Test-Path -LiteralPath $keytool)) { throw 'The Java SDK keytool was not found.' }

$previousStorePassword = $env:OBK_ANDROID_KEYSTORE_PASSWORD
$previousKeyPassword = $env:OBK_ANDROID_KEY_PASSWORD
try {
    if ([string]::IsNullOrEmpty($env:OBK_ANDROID_KEYSTORE_PASSWORD)) {
        $password = Read-Host 'Release keystore password (at least 6 characters)' -AsSecureString
        $env:OBK_ANDROID_KEYSTORE_PASSWORD = [Net.NetworkCredential]::new('', $password).Password
    }
    if ([string]::IsNullOrEmpty($env:OBK_ANDROID_KEY_PASSWORD)) {
        $password = Read-Host 'Release key password (at least 6 characters)' -AsSecureString
        $env:OBK_ANDROID_KEY_PASSWORD = [Net.NetworkCredential]::new('', $password).Password
    }
    if ($env:OBK_ANDROID_KEYSTORE_PASSWORD.Length -lt 6 -or $env:OBK_ANDROID_KEY_PASSWORD.Length -lt 6) {
        throw 'Keystore and key passwords must each have at least 6 characters.'
    }
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    & $keytool -genkeypair -keystore $destination -storetype JKS -alias $KeyAlias `
        -storepass:env OBK_ANDROID_KEYSTORE_PASSWORD -keypass:env OBK_ANDROID_KEY_PASSWORD `
        -keyalg RSA -keysize 4096 -validity 10000 -dname 'CN=OBK Android release' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'Release keystore generation failed. Inspect the destination before retrying.' }
    Write-Output "Release keystore created: $destination"
    Write-Output "Key alias: $KeyAlias"
    Write-Output 'Keep an offline backup of this keystore, alias, and both passwords. Do not share them in chat or Git.'
}
finally {
    $env:OBK_ANDROID_KEYSTORE_PASSWORD = $previousStorePassword
    $env:OBK_ANDROID_KEY_PASSWORD = $previousKeyPassword
}
