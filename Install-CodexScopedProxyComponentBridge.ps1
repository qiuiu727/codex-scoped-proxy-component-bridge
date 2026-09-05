[CmdletBinding()]
param([switch]$LaunchAfterInstall)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'CodexScopedProxyComponentBridge'
$sourceRoot = $PSScriptRoot
$filesToInstall = @(
    'CodexScopedProxyComponents.psm1',
    'Start-CodexWithApprovedComponents.ps1',
    'Start-ApprovedCodexScopedProxyComponents.ps1',
    'Request-CodexScopedProxyComponent.ps1',
    'Manage-CodexScopedProxyComponents.ps1',
    'New-CodexScopedProxyComponentBridgeShortcut.ps1',
    'Uninstall-CodexScopedProxyComponentBridge.ps1'
)

foreach ($file in $filesToInstall) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $file) -PathType Leaf)) {
        throw "Required installer file is missing: $file"
    }
}
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
foreach ($file in $filesToInstall) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination (Join-Path $installRoot $file) -Force
}

& (Join-Path $installRoot 'Start-CodexWithApprovedComponents.ps1') -DetectOnly
if (-not $?) {
    throw 'Installation stopped because no working local HTTP proxy was detected. No system proxy, TUN, or subscription was changed.'
}
& (Join-Path $installRoot 'New-CodexScopedProxyComponentBridgeShortcut.ps1')
if (-not $?) {
    throw 'The bridge was installed, but the Start Menu shortcut could not be created.'
}
Write-Output 'The bridge was installed. Pending components will require approval before they are started.'

if ($LaunchAfterInstall) {
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', (Join-Path $installRoot 'Start-CodexWithApprovedComponents.ps1')) -WindowStyle Hidden
}
