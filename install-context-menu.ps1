$exePath = Join-Path $PSScriptRoot "MultiCopyPrint.exe"
if (-not (Test-Path $exePath)) {
    $exePath = Join-Path $PSScriptRoot "publish\MultiCopyPrint.exe"
}

if (-not (Test-Path $exePath)) {
    Write-Error "MultiCopyPrint.exe not found. Run dotnet publish first."
    exit 1
}

# 1. Register Context Menu for PDFs
$regKey = "HKCU:\Software\Classes\SystemFileAssociations\.pdf\shell\MultiCopyPrint"
New-Item -Path "$regKey\command" -Force | Out-Null
Set-ItemProperty -Path $regKey -Name "(Default)" -Value "Print with Multi-Copy Dialog"
Set-ItemProperty -Path "$regKey\command" -Name "(Default)" -Value "`"$exePath`" `"%1`""

# 2. Provision Shared Spool Folder with Modify ACLs for Users
$spoolDir = "$env:ProgramData\MultiCopyPrint\Spool"
New-Item -Path $spoolDir -ItemType Directory -Force | Out-Null
icacls $spoolDir /grant "*S-1-5-32-545:(OI)(CI)M" /T

Write-Host "Context menu & shared spool directory ACLs successfully installed!"
Write-Host "Spool directory: $spoolDir"
