[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'CodexScopedProxyComponentBridge'
$expectedRoot = [System.IO.Path]::GetFullPath($installRoot).TrimEnd('\')
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Scoped Proxy Component Bridge.lnk'
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
if (Test-Path -LiteralPath $installRoot -PathType Container) {
    $resolved = (Resolve-Path -LiteralPath $installRoot).Path.TrimEnd('\')
    if ($resolved -ne $expectedRoot) {
        throw "Refusing to remove an unexpected path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output 'The component bridge and its local approval records were removed. Codex, proxy core, subscriptions, TUN, and system proxy settings were not changed.'
