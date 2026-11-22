[CmdletBinding(DefaultParameterSetName = 'Signed')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$Files,
    [Parameter(Mandatory)]
    [ValidatePattern('^1\.0\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [Parameter(Mandatory, ParameterSetName = 'Signed')]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Metadata')]
    [switch]$MetadataOnly
)

$ErrorActionPreference = 'Stop'
foreach ($path in $Files) {
    $file = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.VersionInfo.ProductName -cne 'OBK' -or $file.VersionInfo.ProductVersion -cne $Version) {
        throw "Unexpected OBK product/version metadata: $path"
    }
    if (-not $MetadataOnly) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName -ErrorAction Stop
        if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
            throw "Invalid embedded Authenticode signature ($($signature.Status)): $path"
        }
        if ($signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw "Unexpected signing certificate: $path"
        }
        if ($null -eq $signature.TimeStamperCertificate) {
            throw "Missing signature timestamp: $path"
        }
    }
}
Write-Host "Verified $($Files.Count) OBK artifact(s)."
