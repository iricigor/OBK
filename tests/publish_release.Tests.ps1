$ErrorActionPreference = 'Stop'
$publisher = Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\publish_release.ps1'
$calls = [Collections.Generic.List[string]]::new()
$windowsHash = 'a' * 64
$androidHash = 'b' * 64
$androidChecksum = "$androidHash  OBK-1.0.7-android.apk"
$missingApk = $false
$uploadFails = $false
$published = $false
$thumbprint = 'd' * 40
$signatureStatus = 'Valid'

function Get-Item {
    param([string]$LiteralPath, [string]$ErrorAction)
    [pscustomobject]@{ FullName = $LiteralPath; VersionInfo = [pscustomobject]@{ ProductName = 'OBK'; ProductVersion = '1.0.7' } }
}

function Get-AuthenticodeSignature {
    param([string]$LiteralPath, [string]$ErrorAction)
    [pscustomobject]@{
        Status = $signatureStatus; SignatureType = 'Authenticode'
        SignerCertificate = [pscustomobject]@{ Thumbprint = $thumbprint }
        TimeStamperCertificate = [pscustomobject]@{}
    }
}

function Get-FileHash {
    param([string]$LiteralPath, [string]$Algorithm)
    if ($LiteralPath.EndsWith('.apk')) {
        if ($missingApk) { throw 'Missing APK' }
        return [pscustomobject]@{ Hash = $androidHash }
    }
    [pscustomobject]@{ Hash = $windowsHash }
}

function Get-Content {
    param([string]$LiteralPath, [switch]$Raw)
    if ($LiteralPath.EndsWith('ANDROID-SHA256SUMS.txt')) { return $androidChecksum }
    "$windowsHash  OBK-1.0.7-windows-x64-setup.exe"
}

function gh {
    $calls.Add(($args -join ' '))
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'view') { return @{ isDraft = (-not $published) } | ConvertTo-Json }
    if ($args[1] -eq 'upload' -and $uploadFails) { $global:LASTEXITCODE = 1 }
}

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection: $Message"
}

& $publisher -Version '1.0.7'
$upload = @($calls | Where-Object { $_ -like 'release upload *' })
if ($upload.Count -ne 1 -or $upload[0] -notlike '*OBK-1.0.7-windows-x64-setup.exe*SHA256SUMS.txt*OBK-1.0.7-android.apk*ANDROID-SHA256SUMS.txt*') {
    throw 'Both platform assets and their checksum files must be uploaded.'
}
if ($calls[-1] -notlike 'release edit *--draft=false*') { throw 'Publication must follow upload.' }

$calls.Clear()
& $publisher -Version '1.0.7' -SignWindowsRelease -WindowsCertificateThumbprint $thumbprint
if ($calls[-1] -notlike 'release edit *--draft=false*') { throw 'Valid Windows signing must allow publication.' }
$calls.Clear()
$signatureStatus = 'NotSigned'
Assert-Rejected {
    & $publisher -Version '1.0.7' -SignWindowsRelease -WindowsCertificateThumbprint $thumbprint
} 'Invalid embedded Authenticode signature'
if ($calls.Count) { throw 'Invalid Windows signature must block all release operations.' }
$signatureStatus = 'Valid'

$calls.Clear()
$missingApk = $true
Assert-Rejected { & $publisher -Version '1.0.7' } 'Missing APK'
if ($calls.Count) { throw 'Missing APK must block all release operations.' }
$missingApk = $false
$androidChecksum = "$('c' * 64)  OBK-1.0.7-android.apk"
Assert-Rejected { & $publisher -Version '1.0.7' } 'Final APK checksum'
if ($calls.Count) { throw 'Invalid APK checksum must block all release operations.' }
$androidChecksum = "$androidHash  OBK-1.0.7-android.apk"
$uploadFails = $true
Assert-Rejected { & $publisher -Version '1.0.7' } 'release remains draft'
if (@($calls | Where-Object { $_ -like 'release edit *' }).Count) { throw 'Failed upload must not publish a partial release.' }
$uploadFails = $false
$calls.Clear()
$published = $true
Assert-Rejected { & $publisher -Version '1.0.7' } 'published release must not be overwritten'
if (@($calls | Where-Object { $_ -like 'release upload *' -or $_ -like 'release edit *' }).Count) {
    throw 'Published releases must not be modified.'
}
Write-Output 'Release publication tests passed.'
