param(
    [string]$AndroidSdkDirectory = $env:ANDROID_HOME,
    [string]$JavaSdkDirectory = $env:JAVA_HOME
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    throw 'Set ANDROID_HOME or pass -AndroidSdkDirectory.'
}
if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) {
    throw 'Set JAVA_HOME or pass -JavaSdkDirectory.'
}
dotnet build "$root\src\OBK.Android\OBK.Android.csproj" -c Debug `
    "-p:AndroidSdkDirectory=$AndroidSdkDirectory" "-p:JavaSdkDirectory=$JavaSdkDirectory" `
    --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Android preview build failed.' }
$source = "$root\src\OBK.Android\bin\Debug\net10.0-android\cz.obk.reader-Signed.apk"
if (-not (Test-Path -LiteralPath $source)) { throw 'The signed Android preview APK was not produced.' }
New-Item -ItemType Directory -Path "$root\dist" -Force | Out-Null
$destination = "$root\dist\OBK-android-preview.apk"
Copy-Item -LiteralPath $source -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  OBK-android-preview.apk" | Set-Content "$root\dist\ANDROID-SHA256SUMS.txt" -Encoding ascii
Write-Output "Development-signed preview: $destination"
Write-Output 'This is for verification, not production release distribution.'
