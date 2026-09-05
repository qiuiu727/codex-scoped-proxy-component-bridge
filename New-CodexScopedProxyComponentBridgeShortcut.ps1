[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$launcherPath = Join-Path $PSScriptRoot 'Start-CodexWithApprovedComponents.ps1'
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw 'Bridge launcher was not found.'
}
$package = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop | Sort-Object Version -Descending | Select-Object -First 1
if (-not $package) {
    throw 'Microsoft Store Codex was not found.'
}
$appPath = Join-Path $package.InstallLocation 'app\ChatGPT.exe'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Scoped Proxy Component Bridge.lnk'
$powerShellPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $powerShellPath
$shortcut.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcherPath + '"'
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.IconLocation = $appPath + ',0'
$shortcut.Description = 'Start Codex and approved local proxy components'
$shortcut.Save()
Write-Output 'Created the Start Menu shortcut. Pin it to the taskbar if desired.'
