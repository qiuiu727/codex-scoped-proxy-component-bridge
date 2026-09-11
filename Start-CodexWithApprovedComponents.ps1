[CmdletBinding()]
param(
    [string]$ConfigPath,
    [switch]$DetectOnly
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliable while PowerShell evaluates parameter defaults.
# Resolve the default only after the script body begins.
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.json'
}

Import-Module (Join-Path $PSScriptRoot 'CodexScopedProxyComponents.psm1') -Force
Write-ComponentBridgeLog 'BRIDGE_STARTED'

function Test-LocalHttpProxy {
    param([Parameter(Mandatory = $true)][Uri]$ProxyUri)

    if (-not (Test-LoopbackProxyUri -ProxyUri $ProxyUri)) {
        return $false
    }
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $connect = $client.BeginConnect($ProxyUri.Host, $ProxyUri.Port, $null, $null)
            if (-not $connect.AsyncWaitHandle.WaitOne(1500)) {
                return $false
            }
            $client.EndConnect($connect)
            $stream = $client.GetStream()
            $stream.ReadTimeout = 2000
            $request = [System.Text.Encoding]::ASCII.GetBytes("CONNECT chatgpt.com:443 HTTP/1.1`r`nHost: chatgpt.com:443`r`n`r`n")
            $stream.Write($request, 0, $request.Length)
            $buffer = New-Object byte[] 256
            $count = $stream.Read($buffer, 0, $buffer.Length)
            $firstLine = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $count).Split("`r`n")[0]
            return $firstLine -match '^HTTP/1\.[01] 2\d\d'
        }
        finally {
            $client.Dispose()
        }
    }
    catch {
        return $false
    }
}

function Get-ConfiguredProxyUri {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    try {
        $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($config.proxyUrl) -or $config.proxyUrl -match '^<.+>$') {
            return $null
        }
        $proxyUri = [Uri]$config.proxyUrl
        if (Test-LocalHttpProxy -ProxyUri $proxyUri) {
            return $proxyUri
        }
    }
    catch {}
    return $null
}

function Find-LocalHttpProxy {
    $knownCoreProcessPattern = 'clash|mihomo|sing-box|v2ray|xray|nekoray|hiddify'
    $candidates = @()
    foreach ($connection in (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)) {
        if ($connection.LocalAddress -notin @('127.0.0.1', '::1')) {
            continue
        }
        try {
            $process = Get-Process -Id $connection.OwningProcess -ErrorAction Stop
            if ($process.ProcessName -notmatch $knownCoreProcessPattern) {
                continue
            }
            $hostName = if ($connection.LocalAddress -eq '::1') { '[::1]' } else { $connection.LocalAddress }
            $candidates += [Uri]("http://$hostName`:$($connection.LocalPort)")
        }
        catch {}
    }
    foreach ($candidate in ($candidates | Sort-Object AbsoluteUri -Unique)) {
        if (Test-LocalHttpProxy -ProxyUri $candidate) {
            return $candidate
        }
    }
    return $null
}

function Save-ProxyUri {
    param(
        [Parameter(Mandatory = $true)][Uri]$ProxyUri,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $content = [pscustomobject]@{ proxyUrl = $ProxyUri.AbsoluteUri.TrimEnd('/') } | ConvertTo-Json
    [System.IO.File]::WriteAllText($Path, $content + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

function Resolve-ProxyUri {
    param([Parameter(Mandatory = $true)][string]$Path)

    $configured = Get-ConfiguredProxyUri -Path $Path
    if ($configured) {
        return $configured
    }
    $detected = Find-LocalHttpProxy
    if ($detected) {
        Save-ProxyUri -ProxyUri $detected -Path $Path
        return $detected
    }
    throw 'No working local HTTP proxy was detected. Start your local proxy core first; this bridge does not install or configure subscriptions.'
}

function Get-UserApproval {
    param([Parameter(Mandatory = $true)][object]$Request)

    $message = @"
当 Codex 启动时，$($Request.displayName) 请求使用本地代理。

程序：$($Request.executablePath)
启动参数：$($Request.arguments)
文件 SHA-256：$($Request.sha256)

同意后，只有这个路径与校验值一致的文件会在桥接启动时使用本地代理。不会提供 Codex Cookie、登录令牌或账号数据。

是否同意此组件连接？
"@
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        Write-ComponentBridgeLog "APPROVAL_PROMPT_OPEN id=$($Request.id) name=$($Request.displayName)"
        $result = [System.Windows.Forms.MessageBox]::Show(
            $message,
            'Codex 专用代理组件桥接',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning,
            [System.Windows.Forms.MessageBoxDefaultButton]::Button2
        )
        Write-ComponentBridgeLog "APPROVAL_PROMPT_RESULT id=$($Request.id) result=$result"
        return ($result -eq [System.Windows.Forms.DialogResult]::Yes)
    }
    catch {
        Write-ComponentBridgeLog "APPROVAL_DEFERRED error=$($_.Exception.Message)"
        return $null
    }
}

function Process-PendingComponentRequests {
    $pending = Get-PendingComponentRequests
    if (@($pending.requests).Count -eq 0) {
        return
    }
    $approved = Get-ApprovedComponents
    $remaining = @()
    foreach ($request in @($pending.requests)) {
        if (-not (Test-Path -LiteralPath $request.executablePath -PathType Leaf)) {
            Write-ComponentBridgeLog "REQUEST_DISCARDED_MISSING id=$($request.id)"
            continue
        }
        if ((Get-ComponentSha256 -Path $request.executablePath) -ne $request.sha256) {
            Write-ComponentBridgeLog "REQUEST_DISCARDED_HASH_CHANGED id=$($request.id)"
            continue
        }
        $approval = Get-UserApproval -Request $request
        if ($approval -eq $true) {
            $component = [pscustomobject]@{
                id = $request.id
                displayName = $request.displayName
                executablePath = $request.executablePath
                arguments = $request.arguments
                sha256 = $request.sha256
                approvedAt = (Get-Date).ToUniversalTime().ToString('o')
            }
            $approved.components = @($approved.components | Where-Object { -not [string]::Equals($_.executablePath, $request.executablePath, [System.StringComparison]::OrdinalIgnoreCase) }) + @($component)
            if ($request.isUpdate) {
                Write-ComponentBridgeLog "REAPPROVED id=$($request.id) name=$($request.displayName)"
            }
            else {
                Write-ComponentBridgeLog "APPROVED id=$($request.id) name=$($request.displayName)"
            }
        }
        elseif ($null -eq $approval) {
            $remaining += @($request)
            Write-ComponentBridgeLog "REQUEST_DEFERRED id=$($request.id) name=$($request.displayName)"
        }
        else {
            Write-ComponentBridgeLog "REQUEST_DECLINED id=$($request.id) name=$($request.displayName)"
        }
    }
    Save-ApprovedComponents -Document $approved
    $pending.requests = $remaining
    Save-PendingComponentRequests -Document $pending
}

function Show-ExistingCodexWindow {
    $running = @(Get-Process -Name 'ChatGPT' -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return $false
    }
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CodexComponentBridgeWindowApi {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
}
'@ -ErrorAction SilentlyContinue
    foreach ($process in $running) {
        if ($process.MainWindowHandle -ne 0) {
            [CodexComponentBridgeWindowApi]::ShowWindow($process.MainWindowHandle, 9) | Out-Null
            [CodexComponentBridgeWindowApi]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            return $true
        }
    }
    return $true
}

function Start-ApprovedComponents {
    param([Parameter(Mandatory = $true)][Uri]$ProxyUri)

    $runner = Join-Path $PSScriptRoot 'Start-ApprovedCodexScopedProxyComponents.ps1'
    if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
        throw 'Approved-component runner is missing.'
    }
    & $runner -ProxyUri $ProxyUri
    if (-not $?) {
        throw 'The approved-component runner failed.'
    }
}

try {
    $proxyUri = Resolve-ProxyUri -Path $ConfigPath
    if ($DetectOnly) {
        Write-ComponentBridgeLog 'DETECTED local HTTP proxy'
        exit 0
    }

    Process-PendingComponentRequests
    if (Show-ExistingCodexWindow) {
        Start-ApprovedComponents -ProxyUri $proxyUri
        Write-ComponentBridgeLog 'FOCUSED existing Codex window'
        exit 0
    }

    $package = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $package) {
        throw 'Microsoft Store Codex was not found.'
    }
    $appPath = Join-Path $package.InstallLocation 'app\ChatGPT.exe'
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw 'Codex executable was not found in the Microsoft Store package.'
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $appPath
    $startInfo.WorkingDirectory = Split-Path -Parent $appPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = "--proxy-server=$($proxyUri.Host):$($proxyUri.Port) --proxy-bypass-list=<local>"
    Set-ScopedProxyEnvironment -StartInfo $startInfo -ProxyUri $proxyUri
    $process = [System.Diagnostics.Process]::Start($startInfo)
    Start-Sleep -Milliseconds 750
    Start-ApprovedComponents -ProxyUri $proxyUri
    Write-ComponentBridgeLog "STARTED Codex pid=$($process.Id)"
}
catch {
    Write-ComponentBridgeLog "BRIDGE_FAILED error=$($_.Exception.Message)"
    Write-Error $_
    exit 1
}
