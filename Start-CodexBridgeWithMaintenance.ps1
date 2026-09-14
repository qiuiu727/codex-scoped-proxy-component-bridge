[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$maintenanceScript = Join-Path $PSScriptRoot 'Update-CodexCockpitScopedProxyBridge.ps1'
$bridgeScript = Join-Path $PSScriptRoot 'Start-CodexWithApprovedComponents.ps1'

& $maintenanceScript

Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile',
    '-WindowStyle',
    'Hidden',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    $maintenanceScript,
    '-Watch'
) -WindowStyle Hidden

& $bridgeScript
