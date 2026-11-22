$ErrorActionPreference = 'Stop'
$verifier = Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\verify_signatures.ps1'
$thumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
$metadata = [pscustomobject]@{ ProductName = 'OBK'; ProductVersion = '1.0.7' }
$mockSignature = [pscustomobject]@{
    Status = 'Valid'
    SignatureType = 'Authenticode'
    SignerCertificate = [pscustomobject]@{ Thumbprint = $thumbprint }
    TimeStamperCertificate = [pscustomobject]@{}
}

function Get-Item {
    param([string]$LiteralPath, [string]$ErrorAction)
    [pscustomobject]@{ FullName = $LiteralPath; VersionInfo = $metadata }
}

function Get-AuthenticodeSignature {
    param([string]$LiteralPath, [string]$ErrorAction)
    $mockSignature
}

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection: $Message"
}

$parameters = @{ Files = @('OBK.exe', 'OBK.dll', 'OBK.Core.dll'); Version = '1.0.7'; CertificateThumbprint = $thumbprint }
& $verifier @parameters

$mockSignature.Status = 'NotSigned'
Assert-Rejected { & $verifier @parameters } 'Invalid embedded Authenticode signature'
& $verifier -Files 'OBK.exe' -Version '1.0.7' -MetadataOnly
$mockSignature.Status = 'HashMismatch'
Assert-Rejected { & $verifier @parameters } 'Invalid embedded Authenticode signature'
$mockSignature.Status = 'Valid'

$mockSignature.SignerCertificate.Thumbprint = 'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF'
Assert-Rejected { & $verifier @parameters } 'Unexpected signing certificate'
$mockSignature.SignerCertificate.Thumbprint = $thumbprint
$mockSignature.TimeStamperCertificate = $null
Assert-Rejected { & $verifier @parameters } 'Missing signature timestamp'
$mockSignature.TimeStamperCertificate = [pscustomobject]@{}

$metadata.ProductVersion = '1.0.6'
Assert-Rejected { & $verifier @parameters } 'Unexpected OBK product/version metadata'
$metadata.ProductVersion = '1.0.7'
$metadata.ProductName = 'Other project'
Assert-Rejected { & $verifier @parameters } 'Unexpected OBK product/version metadata'
Write-Host 'Signing verification tests passed.'
