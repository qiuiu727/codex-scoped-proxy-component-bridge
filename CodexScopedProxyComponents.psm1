Set-StrictMode -Version Latest

function Get-BridgeDataDirectory {
    $path = Join-Path $PSScriptRoot 'data'
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    return $path
}

function Get-ComponentRegistryPath {
    return (Join-Path (Get-BridgeDataDirectory) 'approved-components.json')
}

function Get-ComponentRequestPath {
    return (Join-Path (Get-BridgeDataDirectory) 'pending-component-requests.json')
}

function Get-ComponentLogPath {
    $directory = Join-Path $PSScriptRoot 'logs'
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    return (Join-Path $directory 'component-bridge.log')
}

function Write-ComponentBridgeLog {
    param([Parameter(Mandatory = $true)][string]$Message)

    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -LiteralPath (Get-ComponentLogPath) -Encoding UTF8 -Value $line
}

function New-EmptyComponentDocument {
    return [pscustomobject]@{
        schemaVersion = 1
        components = @()
    }
}

function New-EmptyRequestDocument {
    return [pscustomobject]@{
        schemaVersion = 1
        requests = @()
    }
}

function Read-BridgeDocument {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][scriptblock]$DefaultFactory,
        [Parameter(Mandatory = $true)][string]$CollectionName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return (& $DefaultFactory)
    }

    $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($document.schemaVersion -ne 1) {
        throw "Unsupported schema in $Path"
    }
    if ($null -eq $document.$CollectionName) {
        $document.$CollectionName = @()
    }
    return $document
}

function Write-BridgeDocument {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Document
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $temporaryPath = "$Path.tmp"
    $json = $Document | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $backupPath = "$Path.replace-backup"
        try {
            [System.IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
        }
        finally {
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
    else {
        [System.IO.File]::Move($temporaryPath, $Path)
    }
}

function Get-ApprovedComponents {
    return (Read-BridgeDocument -Path (Get-ComponentRegistryPath) -DefaultFactory ${function:New-EmptyComponentDocument} -CollectionName 'components')
}

function Save-ApprovedComponents {
    param([Parameter(Mandatory = $true)][object]$Document)
    Write-BridgeDocument -Path (Get-ComponentRegistryPath) -Document $Document
}

function Get-PendingComponentRequests {
    return (Read-BridgeDocument -Path (Get-ComponentRequestPath) -DefaultFactory ${function:New-EmptyRequestDocument} -CollectionName 'requests')
}

function Save-PendingComponentRequests {
    param([Parameter(Mandatory = $true)][object]$Document)
    Write-BridgeDocument -Path (Get-ComponentRequestPath) -Document $Document
}

function Resolve-ComponentExecutablePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Component executable was not found: $Path"
    }
    return ((Resolve-Path -LiteralPath $Path).Path)
}

function Get-ComponentSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant())
}

function Test-LoopbackProxyUri {
    param([Parameter(Mandatory = $true)][Uri]$ProxyUri)

    if ($ProxyUri.Scheme -notin @('http', 'https') -or [string]::IsNullOrWhiteSpace($ProxyUri.Host) -or $ProxyUri.Port -le 0) {
        return $false
    }
    try {
        $addresses = [System.Net.Dns]::GetHostAddresses($ProxyUri.Host)
        return ($addresses.Count -gt 0 -and -not ($addresses | Where-Object { -not [System.Net.IPAddress]::IsLoopback($_) }))
    }
    catch {
        return $false
    }
}

function Set-ScopedProxyEnvironment {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.ProcessStartInfo]$StartInfo,
        [Parameter(Mandatory = $true)][Uri]$ProxyUri
    )

    if (-not (Test-LoopbackProxyUri -ProxyUri $ProxyUri)) {
        throw 'A component may only receive an HTTP proxy on the local loopback interface.'
    }

    $proxyUrl = $ProxyUri.AbsoluteUri.TrimEnd('/')
    $StartInfo.EnvironmentVariables['HTTP_PROXY'] = $proxyUrl
    $StartInfo.EnvironmentVariables['HTTPS_PROXY'] = $proxyUrl
    $StartInfo.EnvironmentVariables['http_proxy'] = $proxyUrl
    $StartInfo.EnvironmentVariables['https_proxy'] = $proxyUrl
    $StartInfo.EnvironmentVariables['NO_PROXY'] = 'localhost,127.0.0.1,::1'
    $StartInfo.EnvironmentVariables['no_proxy'] = 'localhost,127.0.0.1,::1'
    $StartInfo.EnvironmentVariables.Remove('ALL_PROXY')
    $StartInfo.EnvironmentVariables.Remove('all_proxy')
}

function Test-ComponentAlreadyRunning {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -and [string]::Equals($process.Path, $ExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        catch {}
    }
    return $false
}

Export-ModuleMember -Function Get-ApprovedComponents,Save-ApprovedComponents,Get-PendingComponentRequests,Save-PendingComponentRequests,Resolve-ComponentExecutablePath,Get-ComponentSha256,Test-LoopbackProxyUri,Set-ScopedProxyEnvironment,Test-ComponentAlreadyRunning,Write-ComponentBridgeLog
