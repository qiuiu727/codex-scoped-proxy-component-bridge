[CmdletBinding()]
param(
    [switch]$Watch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$bridgeDir = $PSScriptRoot
$cockpitPath = Join-Path $env:LOCALAPPDATA 'Cockpit Tools\cockpit-tools.exe'
$approvedPath = Join-Path $bridgeDir 'data\approved-components.json'
$requestScript = Join-Path $bridgeDir 'Request-CodexScopedProxyComponent.ps1'
$runnerVbs = Join-Path $bridgeDir 'Run-CodexBridgeWithMaintenance.vbs'
$statePath = Join-Path $bridgeDir 'data\bridge-maintenance-state.json'
$logPath = Join-Path $bridgeDir 'logs\bridge-maintenance.log'
$promptCooldownMinutes = 60

function Write-MaintenanceLog {
    param([string]$Message)
    Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format 's') $Message" -Encoding utf8
}

function Read-MaintenanceState {
    if (-not (Test-Path -LiteralPath $statePath)) {
        return [pscustomobject]@{
            codexPackageVersion = ''
            lastPromptedCockpitHash = ''
            lastPromptedAt = ''
        }
    }
    try {
        return Get-Content -LiteralPath $statePath -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        Write-MaintenanceLog "State reset after read error: $($_.Exception.Message)"
        return [pscustomobject]@{
            codexPackageVersion = ''
            lastPromptedCockpitHash = ''
            lastPromptedAt = ''
        }
    }
}

function Save-MaintenanceState {
    param([Parameter(Mandatory = $true)][object]$State)
    $json = $State | ConvertTo-Json
    [System.IO.File]::WriteAllText($statePath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Get-CodexPackage {
    Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

function Ensure-BridgeShortcuts {
    param([Parameter(Mandatory = $true)][object]$Package)

    if (-not (Test-Path -LiteralPath $runnerVbs -PathType Leaf)) {
        throw "Bridge maintenance runner was not found: $runnerVbs"
    }

    $programsDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $startupDir = Join-Path $programsDir 'Startup'
    $wscriptPath = Join-Path $env:WINDIR 'System32\wscript.exe'
    $iconPath = Join-Path $Package.InstallLocation 'app\ChatGPT.exe'
    $shell = New-Object -ComObject WScript.Shell

    $shortcutPath = Join-Path $programsDir 'Codex Scoped Proxy Component Bridge.lnk'
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $wscriptPath
    $shortcut.Arguments = '"' + $runnerVbs + '"'
    $shortcut.WorkingDirectory = $bridgeDir
    $shortcut.IconLocation = $iconPath + ',0'
    $shortcut.Description = 'Start Codex and Cockpit through the approved scoped proxy bridge'
    $shortcut.Save()

    $startupShortcut = Join-Path $startupDir 'Codex Scoped Proxy Component Bridge.lnk'
    if (Test-Path -LiteralPath $startupShortcut -PathType Leaf) {
        Remove-Item -LiteralPath $startupShortcut -Force
        Write-MaintenanceLog 'Removed duplicate bridge startup shortcut; the ordered Codex startup entry is authoritative.'
    }
}

function Test-CockpitApproval {
    param([Parameter(Mandatory = $true)][string]$CurrentHash)

    if (-not (Test-Path -LiteralPath $approvedPath -PathType Leaf)) {
        return $false
    }
    try {
        $document = Get-Content -LiteralPath $approvedPath -Raw -Encoding utf8 | ConvertFrom-Json
        foreach ($component in @($document.components)) {
            if ($component.executablePath -and
                $component.executablePath.Equals($cockpitPath, [System.StringComparison]::OrdinalIgnoreCase) -and
                $component.sha256 -and
                $component.sha256.Equals($CurrentHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }
    catch {
        Write-MaintenanceLog "Approval registry read error: $($_.Exception.Message)"
    }
    return $false
}

function Invoke-BridgeMaintenance {
    $state = Read-MaintenanceState
    $package = Get-CodexPackage
    $version = [string]$package.Version

    Ensure-BridgeShortcuts -Package $package
    if ($state.codexPackageVersion -ne $version) {
        $state.codexPackageVersion = $version
        Write-MaintenanceLog "Codex package updated or first seen: $version"
    }

    if (-not (Test-Path -LiteralPath $cockpitPath -PathType Leaf)) {
        Write-MaintenanceLog "Cockpit executable not found: $cockpitPath"
        Save-MaintenanceState -State $state
        return
    }

    $currentHash = (Get-FileHash -LiteralPath $cockpitPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-CockpitApproval -CurrentHash $currentHash) {
        $state.lastPromptedCockpitHash = ''
        $state.lastPromptedAt = ''
        Save-MaintenanceState -State $state
        return
    }

    $lastPromptAt = [datetime]::MinValue
    if ($state.lastPromptedAt) {
        [void][datetime]::TryParse([string]$state.lastPromptedAt, [ref]$lastPromptAt)
    }
    $sameHash = $state.lastPromptedCockpitHash -eq $currentHash
    $withinCooldown = $sameHash -and (((Get-Date) - $lastPromptAt).TotalMinutes -lt $promptCooldownMinutes)
    if ($withinCooldown) {
        Save-MaintenanceState -State $state
        return
    }

    $state.lastPromptedCockpitHash = $currentHash
    $state.lastPromptedAt = (Get-Date).ToString('o')
    Save-MaintenanceState -State $state
    Write-MaintenanceLog 'Cockpit executable changed. Opening the hash approval prompt.'

    $approvalHost = (Get-Command pwsh -ErrorAction Stop).Source
    $approvalStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $approvalStartInfo.FileName = $approvalHost
    $approvalStartInfo.UseShellExecute = $false
    $approvalStartInfo.CreateNoWindow = $true
    foreach ($argument in @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $requestScript,
        '-ExecutablePath', $cockpitPath,
        '-DisplayName', 'Cockpit Tools (updated)',
        '-PromptNow'
    )) {
        [void]$approvalStartInfo.ArgumentList.Add($argument)
    }
    $approvalProcess = [System.Diagnostics.Process]::Start($approvalStartInfo)
    if ($null -eq $approvalProcess) {
        throw 'Could not start the Cockpit update approval process.'
    }
    Write-MaintenanceLog "Cockpit update approval prompt started in foreground pid=$($approvalProcess.Id)."
}

try {
    Invoke-BridgeMaintenance
}
catch {
    Write-MaintenanceLog "Maintenance error: $($_.Exception.Message)"
    throw
}

if ($Watch) {
    $watchMutex = [System.Threading.Mutex]::new($false, 'Local\CodexCockpitScopedProxyBridgeMaintenance')
    if (-not $watchMutex.WaitOne(0)) {
        Write-MaintenanceLog 'A maintenance watcher is already running.'
        exit 0
    }
    try {
        while ($true) {
            Start-Sleep -Seconds 60
            try {
                Invoke-BridgeMaintenance
            }
            catch {
                Write-MaintenanceLog "Watch iteration error: $($_.Exception.Message)"
            }
        }
    }
    finally {
        $watchMutex.ReleaseMutex()
        $watchMutex.Dispose()
    }
}
