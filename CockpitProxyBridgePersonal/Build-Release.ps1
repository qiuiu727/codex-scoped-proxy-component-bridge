[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler missing.' }
$launcher = Join-Path $PSScriptRoot 'dist\CodexBridgeLauncher.exe'
$setup = Join-Path $PSScriptRoot 'dist\CodexProxyBridge-Setup.exe'
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$icon = Join-Path $PSScriptRoot 'CodexBridgeLauncher.ico'
$watcherSource = Join-Path $PSScriptRoot 'Installer\WatcherTask.cs'
$hookSource = Join-Path $PSScriptRoot 'Installer\CockpitHook.cs'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$launcher" "/win32icon:$icon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'CodexBridgeLauncher\Program.cs') (Join-Path $PSScriptRoot 'CodexBridgeLauncher\TrayRuntime.cs') (Join-Path $PSScriptRoot 'Installer\TaskbarShortcuts.cs') $watcherSource $hookSource
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$setup" "/win32icon:$icon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll "/resource:$launcher,CodexBridgeLauncher.exe" (Join-Path $PSScriptRoot 'Installer\Program.cs') (Join-Path $PSScriptRoot 'Installer\TaskbarShortcuts.cs') $watcherSource $hookSource
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$test = Start-Process -FilePath $setup -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if ($test.ExitCode -ne 0) { throw "Installer self-test failed: $($test.ExitCode)" }
Write-Output 'Built launcher and installer; embedded payload self-test passed.'
