[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler missing.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$launcher = Join-Path $output 'ScopedProxyLauncher.exe'
$setup = Join-Path $output 'ScopedProxyLauncher-Setup.exe'
$test = Join-Path $output 'Regression.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$launcher" /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$setup" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll "/resource:$launcher,ScopedProxyLauncher.exe" (Join-Path $PSScriptRoot 'Setup.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installerTest = Start-Process -FilePath $setup -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
if ($installerTest.ExitCode -ne 0) { throw 'Installer self-test failed.' }
& $compiler /nologo /target:exe /platform:x64 "/out:$test" (Join-Path $PSScriptRoot 'Tests\Regression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Regression compilation failed.' }
& $test $launcher
if ($LASTEXITCODE -ne 0) { throw 'Launcher regression failed.' }
Remove-Item -LiteralPath $test -Force
Write-Output "Built: $setup"
