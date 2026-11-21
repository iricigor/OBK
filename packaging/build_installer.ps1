param(
    [Parameter(Mandatory)]
    [ValidatePattern('^0\.1\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [string]$Iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Iscc -PathType Leaf)) {
    throw "Inno Setup 6 compiler not found: $Iscc"
}
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'dist\stage'
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
dotnet publish (Join-Path $root 'src\OBK.App\OBK.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $stage --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Self-contained application publish failed.' }
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') $stage
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
$dotnetRoot = Split-Path (Get-Command dotnet -ErrorAction Stop).Source -Parent
Copy-Item (Join-Path $dotnetRoot 'LICENSE.txt') (Join-Path $licenses 'dotnet-LICENSE.txt')
Copy-Item (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') (Join-Path $licenses 'dotnet-ThirdPartyNotices.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/AngleSharp/AngleSharp/v1.10.5/LICENSE' `
    -OutFile (Join-Path $licenses 'AngleSharp-LICENSE.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/ed046114d5a30534e13294d94d78eb73de896ad4/LICENSE.TXT' `
    -OutFile (Join-Path $licenses 'SQLitePCLRaw-LICENSE.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/ed046114d5a30534e13294d94d78eb73de896ad4/NOTICE.TXT' `
    -OutFile (Join-Path $licenses 'SQLitePCLRaw-NOTICE.txt')

$result = Join-Path $root 'dist\publish-smoke.json'
$process = Start-Process (Join-Path $stage 'OBK.exe') `
    -ArgumentList @('--smoke-test', '--smoke-result', "`"$result`"") -PassThru
if (-not $process.WaitForExit(120000)) {
    Stop-Process -Id $process.Id -Force
    throw 'Published application smoke test timed out.'
}
if ($process.ExitCode -ne 0 -or -not (Test-Path $result) -or -not (Get-Content $result -Raw | ConvertFrom-Json).ok) {
    throw 'Published application smoke test failed.'
}
Remove-Item -LiteralPath $result
& $Iscc '/Q' "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $root "dist\OBK-$Version-windows-x64-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Compiler did not produce $installer" }
Write-Host $installer
