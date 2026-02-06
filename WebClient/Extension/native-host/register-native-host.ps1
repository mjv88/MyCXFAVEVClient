#Requires -Version 5.1
<#
.SYNOPSIS
    Registers the 3CX-DATEV Bridge Native Messaging Host with Chrome and/or Edge.

.DESCRIPTION
    This script:
    1. Locates 3CXDatevNativeHost.exe (auto-detect or manual path)
    2. Asks for the browser extension ID(s)
    3. Generates the native messaging manifest JSON with correct paths
    4. Creates the Windows registry keys so Chrome/Edge can find the host

.PARAMETER ExePath
    Optional. Absolute path to 3CXDatevNativeHost.exe.
    If omitted, the script searches common locations.

.PARAMETER ExtensionId
    Optional. Chrome/Edge extension ID (32-char lowercase string from chrome://extensions).
    If omitted, the script will prompt for it.

.EXAMPLE
    .\register-native-host.ps1
    .\register-native-host.ps1 -ExtensionId "abcdefghijklmnopabcdefghijklmnop"
    .\register-native-host.ps1 -ExePath "C:\MyApp\3CXDatevNativeHost.exe" -ExtensionId "abcdefghijklmnop..."
#>
param(
    [string]$ExePath = "",
    [string]$ExtensionId = ""
)

$ErrorActionPreference = "Stop"
$hostName = "com.mjv88.datevbridge"

# --- Locate the native host exe ---

function Find-NativeHostExe {
    $candidates = @(
        # Same directory as this script
        (Join-Path $PSScriptRoot "3CXDatevNativeHost.exe"),
        # Build output (Debug)
        (Join-Path $PSScriptRoot "..\..\..\3CXDatevNativeHost\bin\Debug\3CXDatevNativeHost.exe"),
        # Build output (Release)
        (Join-Path $PSScriptRoot "..\..\..\3CXDatevNativeHost\bin\Release\3CXDatevNativeHost.exe"),
        # Install location
        (Join-Path $env:LOCALAPPDATA "Programs\3CXDATEVBridge\3CXDatevNativeHost.exe"),
        # Program Files
        (Join-Path $env:ProgramFiles "3CXDATEVBridge\3CXDatevNativeHost.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "3CXDATEVBridge\3CXDatevNativeHost.exe")
    )

    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) {
            return (Resolve-Path $path).Path
        }
    }
    return $null
}

if ($ExePath -and (Test-Path $ExePath)) {
    $exeFullPath = (Resolve-Path $ExePath).Path
} elseif ($ExePath) {
    Write-Error "Specified ExePath not found: $ExePath"
    exit 1
} else {
    $exeFullPath = Find-NativeHostExe
    if (-not $exeFullPath) {
        Write-Host ""
        Write-Host "Could not auto-detect 3CXDatevNativeHost.exe." -ForegroundColor Yellow
        Write-Host "Please build the solution first, or provide the path manually:"
        Write-Host "  .\register-native-host.ps1 -ExePath 'C:\path\to\3CXDatevNativeHost.exe'"
        Write-Host ""
        exit 1
    }
}

Write-Host "Native host exe: $exeFullPath" -ForegroundColor Green

# --- Get extension ID ---

if (-not $ExtensionId) {
    Write-Host ""
    Write-Host "To find your extension ID:" -ForegroundColor Cyan
    Write-Host "  1. Open chrome://extensions (or edge://extensions)"
    Write-Host "  2. Enable 'Developer mode' (toggle in top-right)"
    Write-Host "  3. Find '3CX DATEV Bridge Connector'"
    Write-Host "  4. Copy the ID (32-character string like 'abcdefghijklmnop...')"
    Write-Host ""
    $ExtensionId = Read-Host "Enter the extension ID"
}

$ExtensionId = $ExtensionId.Trim().ToLower()

if ($ExtensionId.Length -ne 32 -or $ExtensionId -notmatch '^[a-p]{32}$') {
    Write-Warning "Extension ID '$ExtensionId' doesn't look like a standard Chrome extension ID (expected 32 lowercase a-p chars)."
    $confirm = Read-Host "Continue anyway? (y/n)"
    if ($confirm -ne 'y') { exit 0 }
}

# --- Generate the manifest JSON ---

$manifestDir = Join-Path $env:LOCALAPPDATA "3CXDATEVBridge"
if (-not (Test-Path $manifestDir)) {
    New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
}

$manifestPath = Join-Path $manifestDir "$hostName.json"

# Escape backslashes for JSON
$exeJsonPath = $exeFullPath.Replace('\', '\\')

$manifestContent = @"
{
  "name": "$hostName",
  "description": "3CX DATEV Bridge - Native Messaging relay to bridge named pipe",
  "path": "$exeJsonPath",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://$ExtensionId/"
  ]
}
"@

Set-Content -Path $manifestPath -Value $manifestContent -Encoding UTF8
Write-Host "Manifest written: $manifestPath" -ForegroundColor Green

# --- Register with Chrome and Edge ---

$chromeKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
$edgeKey = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"

# Chrome
New-Item -Path $chromeKey -Force | Out-Null
Set-ItemProperty -Path $chromeKey -Name '(Default)' -Value $manifestPath
Write-Host "Chrome registry key set: $chromeKey" -ForegroundColor Green

# Edge (uses same manifest format)
New-Item -Path $edgeKey -Force | Out-Null
Set-ItemProperty -Path $edgeKey -Name '(Default)' -Value $manifestPath
Write-Host "Edge registry key set:   $edgeKey" -ForegroundColor Green

# --- Verify ---

Write-Host ""
Write-Host "=== Registration Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Manifest content:"
Get-Content $manifestPath | Write-Host
Write-Host ""
Write-Host "IMPORTANT: Restart Chrome/Edge completely for the registration to take effect." -ForegroundColor Yellow
Write-Host "  (Close ALL browser windows, then re-open)"
Write-Host ""
