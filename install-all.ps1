#Requires -RunAsAdministrator
# MultiCopyPrint Production Installer Script (Option A: Direct PDF + Watched Hotfolder)

$ErrorActionPreference = 'Stop'
$installDir = "C:\Program Files\MultiCopyPrint"
$spoolDir = "$env:ProgramData\MultiCopyPrint\Spool"
$publicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "  Installing MultiCopyPrint Production   " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

# 1. Install to Program Files
Write-Host "[1/5] Copying application files to $installDir..."
if (-not (Test-Path $installDir)) {
    New-Item -Path $installDir -ItemType Directory -Force | Out-Null
}

$sourceFiles = $PSScriptRoot
if (-not (Test-Path (Join-Path $sourceFiles "MultiCopyPrint.exe"))) {
    if (Test-Path (Join-Path $sourceFiles "publish\MultiCopyPrint.exe")) {
        $sourceFiles = Join-Path $sourceFiles "publish"
    }
}

Copy-Item -Path "$sourceFiles\*" -Destination $installDir -Recurse -Force | Out-Null

$targetExe = Join-Path $installDir "MultiCopyPrint.exe"
if (-not (Test-Path $targetExe)) {
    Write-Error "MultiCopyPrint.exe failed to copy to $installDir"
    exit 1
}

# 2. Provision Shared Spool Folder with Modify ACLs for all Users
Write-Host "[2/5] Provisioning shared spool folder: $spoolDir..."
if (-not (Test-Path $spoolDir)) {
    New-Item -Path $spoolDir -ItemType Directory -Force | Out-Null
}
icacls $spoolDir /grant "*S-1-5-32-545:(OI)(CI)M" /T | Out-Null

# 3. Register Machine-Wide Explorer Right-Click Menu (HKLM)
Write-Host "[3/5] Registering machine-wide PDF context menu..."
$regKey = "HKLM:\Software\Classes\SystemFileAssociations\.pdf\shell\MultiCopyPrint"
New-Item -Path "$regKey\command" -Force | Out-Null
Set-ItemProperty -Path $regKey -Name "(Default)" -Value "Print with Multi-Copy Dialog"
Set-ItemProperty -Path "$regKey\command" -Name "(Default)" -Value "`"$targetExe`" `"%1`""

# 4. Register Machine Startup Auto-Run (Optional background watcher)
Write-Host "[4/5] Registering startup entry..."
$runKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name "MultiCopyPrint" -Value "`"$targetExe`""

# 5. Create Desktop Shortcuts on Public Desktop for All Users
Write-Host "[5/5] Creating Desktop shortcuts on Public Desktop..."
$wsh = New-Object -ComObject WScript.Shell

# Shortcut 1: Main Application
$appLnk = Join-Path $publicDesktop "MultiCopy Print.lnk"
$shortcut1 = $wsh.CreateShortcut($appLnk)
$shortcut1.TargetPath = $targetExe
$shortcut1.WorkingDirectory = $installDir
$shortcut1.Description = "MultiCopy Print Manager"
$shortcut1.Save()

# Shortcut 2: Corel / Hotfolder Drop Zone
$hotfolderLnk = Join-Path $publicDesktop "MultiCopy Hotfolder (Save Corel PDF Here).lnk"
$shortcut2 = $wsh.CreateShortcut($hotfolderLnk)
$shortcut2.TargetPath = $spoolDir
$shortcut2.Description = "Save CorelDRAW or Acrobat PDFs here for automatic multi-copy print dialog"
$shortcut2.Save()

Write-Host "=========================================" -ForegroundColor Green
Write-Host "  Installation Completed Successfully!   " -ForegroundColor Green
Write-Host "  Installed at: $installDir              " -ForegroundColor White
Write-Host "  Shortcuts created on Desktop for all users" -ForegroundColor White
Write-Host "=========================================" -ForegroundColor Green
