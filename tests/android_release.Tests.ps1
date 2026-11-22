param(
    [string]$AndroidSdkDirectory = $env:ANDROID_HOME,
    [string]$JavaSdkDirectory = $env:JAVA_HOME
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$builder = Join-Path $root 'packaging\build_android_release.ps1'
$verifier = Join-Path $root 'packaging\verify_android_release.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) "obk-signing-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporary | Out-Null
$previousStorePassword = $env:OBK_ANDROID_KEYSTORE_PASSWORD
$previousKeyPassword = $env:OBK_ANDROID_KEY_PASSWORD

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection: $Message"
}

try {
    $env:OBK_ANDROID_KEYSTORE_PASSWORD = [guid]::NewGuid().ToString('N')
    $env:OBK_ANDROID_KEY_PASSWORD = [guid]::NewGuid().ToString('N')
    $keystore = Join-Path $temporary 'test.keystore'
    $alias = 'obk-test'
    & "$root\packaging\new_android_keystore.ps1" -DestinationPath $keystore -KeyAlias $alias -JavaSdkDirectory $JavaSdkDirectory
    Assert-Rejected {
        & "$root\packaging\new_android_keystore.ps1" -DestinationPath $keystore -KeyAlias $alias -JavaSdkDirectory $JavaSdkDirectory
    } 'already exists'
    $parameters = @{
        Version = '1.0.7'; KeystorePath = $keystore; KeyAlias = $alias
        OutputDirectory = (Join-Path $temporary 'dist')
        AndroidSdkDirectory = $AndroidSdkDirectory; JavaSdkDirectory = $JavaSdkDirectory
    }
    $password = $env:OBK_ANDROID_KEY_PASSWORD
    $env:OBK_ANDROID_KEY_PASSWORD = ''
    Assert-Rejected { & $builder @parameters } 'no development signing fallback'
    $env:OBK_ANDROID_KEY_PASSWORD = $password
    $parameters.Version = '1.0.2100000000'
    Assert-Rejected { & $builder @parameters } 'version code exceeds'
    $parameters.Version = '1.0.7'
    $parameters.KeyAlias = 'missing-key'
    Assert-Rejected { & $builder @parameters } 'Cannot read the release certificate'
    $parameters.KeyAlias = $alias
    $storePassword = $env:OBK_ANDROID_KEYSTORE_PASSWORD
    $env:OBK_ANDROID_KEYSTORE_PASSWORD = 'invalid-test-password'
    Assert-Rejected { & $builder @parameters } 'Cannot read the release certificate'
    $env:OBK_ANDROID_KEYSTORE_PASSWORD = $storePassword
    $env:OBK_ANDROID_KEY_PASSWORD = 'invalid-test-password'
    Assert-Rejected { & $builder @parameters } 'Cannot unlock the release signing key'
    $env:OBK_ANDROID_KEY_PASSWORD = $password

    $certificate = Join-Path $temporary 'test.der'
    & (Join-Path $JavaSdkDirectory 'bin\keytool.exe') -exportcert -keystore $keystore -alias $alias `
        -storepass:env OBK_ANDROID_KEYSTORE_PASSWORD -file $certificate
    if ($LASTEXITCODE -ne 0) { throw 'Cannot export test certificate.' }
    $certificateSha256 = (Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash
    foreach ($version in @('1.0.7', '1.0.8')) {
        $parameters.Version = $version
        & $builder @parameters
        $apk = Join-Path $parameters.OutputDirectory "OBK-$version-android.apk"
        $expected = "$((Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant())  OBK-$version-android.apk"
        if ((Get-Content -LiteralPath (Join-Path $parameters.OutputDirectory 'ANDROID-SHA256SUMS.txt') -Raw).Trim() -cne $expected) {
            throw 'Release APK checksum does not match.'
        }
        & $verifier -ApkPath $apk -Version $version -CertificateSha256 $certificateSha256 `
            -AndroidSdkDirectory $AndroidSdkDirectory -JavaSdkDirectory $JavaSdkDirectory
    }
    $verification = @{
        ApkPath = $apk; Version = '1.0.8'; CertificateSha256 = ('0' * 64)
        AndroidSdkDirectory = $AndroidSdkDirectory; JavaSdkDirectory = $JavaSdkDirectory
    }
    Assert-Rejected { & $verifier @verification } 'expected release certificate'
    $verification.CertificateSha256 = $certificateSha256
    $verification.Version = '1.0.7'
    Assert-Rejected { & $verifier @verification } 'package identity or release version'
    $verification.Version = '1.0.8'
    $damaged = Join-Path $temporary 'damaged.apk'
    [IO.File]::WriteAllBytes($damaged, [byte[]]@(0, 1, 2, 3))
    $verification.ApkPath = $damaged
    Assert-Rejected { & $verifier @verification } 'signature verification failed'

    $assembly = Join-Path $root 'src\OBK.Android\bin\Release\net10.0-android\OBK.Android.dll'
    $stream = [IO.File]::OpenRead($assembly)
    $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
        foreach ($handle in $metadata.TypeDefinitions) {
            $type = $metadata.GetTypeDefinition($handle)
            if ($metadata.GetString($type.Name) -eq 'VerificationData') {
                throw 'Debug verification fixture leaked into the Release assembly.'
            }
        }
        $handle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::UserStringHandle(1)
        while (-not $handle.IsNil) {
            if ($metadata.GetUserString($handle) -eq 'obk_verification') {
                throw 'Debug verification launch handling leaked into the Release assembly.'
            }
            $handle = [System.Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetNextHandle($metadata, $handle)
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
    Write-Output 'Android release signing tests passed: consecutive versions, checksums, fixture exclusion, and signing failures.'
}
finally {
    $env:OBK_ANDROID_KEYSTORE_PASSWORD = $previousStorePassword
    $env:OBK_ANDROID_KEY_PASSWORD = $previousKeyPassword
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
