param(
    [Parameter(Mandatory)]
    [string]$Installer
)

$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetTempPath()) ("OBK installer check " + [guid]::NewGuid())
$install = Join-Path $directory 'app'
$result = Join-Path $directory 'result.json'
New-Item -ItemType Directory -Path $directory | Out-Null

function Invoke-CheckedProcess([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id -Force
        throw "Process timed out: $File"
    }
    if ($process.ExitCode -ne 0) {
        throw "Process failed with exit code $($process.ExitCode): $File"
    }
}

try {
    Invoke-CheckedProcess (Resolve-Path $Installer).Path @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS',
        "/VerificationId=$([guid]::NewGuid())",
        "/DIR=`"$install`"", "/LOG=`"$(Join-Path $directory 'install.log')`""
    )
    $uninstaller = Join-Path $install 'unins000.exe'
    if (-not (Test-Path $uninstaller)) {
        throw 'Installer did not create the uninstaller.'
    }
    # Use the same windowed runtime and arguments as the Start-menu shortcut.
    Invoke-CheckedProcess (Join-Path $install 'runtime\pythonw.exe') @(
        '-I', "`"$(Join-Path $install 'app\obk_app.py')`"",
        '--smoke-test', '--smoke-result', "`"$result`""
    )
    if (-not (Test-Path $result) -or -not (Get-Content $result -Raw | ConvertFrom-Json).ok) {
        throw 'Installed application did not report a successful smoke test.'
    }
    Invoke-CheckedProcess $uninstaller @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/LOG=`"$(Join-Path $directory 'uninstall.log')`""
    )
    if (Test-Path (Join-Path $install 'runtime\pythonw.exe')) {
        throw 'Uninstall left the application runtime behind.'
    }
    # Inno's copied uninstaller can briefly hold the log after its launcher exits.
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (Test-Path $directory) {
        try {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
    Write-Host 'Installer install/start/uninstall verification passed.'
}
catch {
    Write-Host "Verification files retained for diagnosis: $directory"
    Get-ChildItem $directory -Filter '*.log' | ForEach-Object { Get-Content $_.FullName }
    throw
}
