[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$launcherPath = Join-Path $PSScriptRoot 'Run-CodexScopedProxyComponentBridge.vbs'
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw 'Bridge launcher was not found.'
}
$package = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop | Sort-Object Version -Descending | Select-Object -First 1
if (-not $package) {
    throw 'Microsoft Store Codex was not found.'
}
$appPath = Join-Path $package.InstallLocation 'app\ChatGPT.exe'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Scoped Proxy Component Bridge.lnk'
$wscriptPath = Join-Path $env:WINDIR 'System32\wscript.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $wscriptPath
$shortcut.Arguments = '"' + $launcherPath + '"'
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.IconLocation = $appPath + ',0'
$shortcut.Description = 'Start Codex and approved local proxy components'
$shortcut.Save()
Write-Output 'Created the Start Menu shortcut. Pin it to the taskbar if desired.'
