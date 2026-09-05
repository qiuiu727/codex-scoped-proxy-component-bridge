[CmdletBinding()]
param([Parameter(Mandatory = $true)][Uri]$ProxyUri)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CodexScopedProxyComponents.psm1') -Force

try {
    if (-not (Test-LoopbackProxyUri -ProxyUri $ProxyUri)) {
        throw 'The proxy URI is not a valid loopback HTTP proxy.'
    }

    $registry = Get-ApprovedComponents
    foreach ($component in @($registry.components)) {
        if (-not (Test-Path -LiteralPath $component.executablePath -PathType Leaf)) {
            Write-ComponentBridgeLog "SKIPPED_MISSING id=$($component.id)"
            continue
        }
        if ((Get-ComponentSha256 -Path $component.executablePath) -ne $component.sha256) {
            Write-ComponentBridgeLog "SKIPPED_HASH_CHANGED id=$($component.id)"
            continue
        }
        if (Test-ComponentAlreadyRunning -ExecutablePath $component.executablePath) {
            Write-ComponentBridgeLog "ALREADY_RUNNING id=$($component.id)"
            continue
        }

        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $component.executablePath
        $startInfo.Arguments = [string]$component.arguments
        $startInfo.WorkingDirectory = Split-Path -Parent $component.executablePath
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        Set-ScopedProxyEnvironment -StartInfo $startInfo -ProxyUri $ProxyUri
        $process = [System.Diagnostics.Process]::Start($startInfo)
        Write-ComponentBridgeLog "STARTED id=$($component.id) pid=$($process.Id)"
    }
}
catch {
    Write-ComponentBridgeLog "START_APPROVED_FAILED error=$($_.Exception.Message)"
    Write-Error $_
    exit 1
}
