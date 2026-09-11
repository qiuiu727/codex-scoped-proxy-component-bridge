[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$Arguments = '',
    [string]$DisplayName,
    [switch]$PromptNow
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CodexScopedProxyComponents.psm1') -Force

function Get-ImmediateApproval {
    param([Parameter(Mandatory = $true)]$Request)

    $message = @"
$(if ($Request.isUpdate) { "组件 $($Request.displayName) 的程序文件已更新，需要重新审批。" } else { "组件 $($Request.displayName) 请求通过 Codex 专用代理桥接启动。" })

程序：$($Request.executablePath)
启动参数：$($Request.arguments)
文件 SHA-256：$($Request.sha256)

同意后，只有这个路径与校验值一致的文件会在桥接启动时使用本地代理。不会提供 Codex Cookie、登录令牌或账号数据。

$(if ($Request.isUpdate) { '是否同意更新此组件的授权？' } else { '是否添加此组件？' })
"@
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $result = [System.Windows.Forms.MessageBox]::Show(
            $message,
            'Codex 专用代理组件桥接',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning,
            [System.Windows.Forms.MessageBoxDefaultButton]::Button2
        )
        return [pscustomobject]@{
            Available = $true
            Approved = ($result -eq [System.Windows.Forms.DialogResult]::Yes)
        }
    }
    catch {
        Write-ComponentBridgeLog 'APPROVAL_DEFERRED Windows Forms prompt unavailable'
        return [pscustomobject]@{
            Available = $false
            Approved = $false
        }
    }
}

try {
    $resolvedPath = Resolve-ComponentExecutablePath -Path $ExecutablePath
    if ([string]::IsNullOrWhiteSpace($DisplayName)) {
        $DisplayName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedPath)
    }

    $pending = Get-PendingComponentRequests
    $approved = Get-ApprovedComponents
    $currentSha256 = Get-ComponentSha256 -Path $resolvedPath
    $existingComponents = @($approved.components | Where-Object { [string]::Equals($_.executablePath, $resolvedPath, [System.StringComparison]::OrdinalIgnoreCase) })
    $existingComponent = @($existingComponents | Select-Object -First 1)
    $isUpdate = $existingComponent.Count -gt 0
    if ($isUpdate -and $existingComponent[0].sha256 -eq $currentSha256) {
        throw 'This executable is already approved with its current file hash.'
    }
    if (@($pending.requests | Where-Object { [string]::Equals($_.executablePath, $resolvedPath, [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        throw 'A request for this executable is already pending approval.'
    }

    $request = [pscustomobject]@{
        id = if ($isUpdate) { $existingComponent[0].id } else { [guid]::NewGuid().Guid }
        displayName = $DisplayName
        executablePath = $resolvedPath
        arguments = $Arguments
        sha256 = $currentSha256
        requestedAt = (Get-Date).ToUniversalTime().ToString('o')
        isUpdate = $isUpdate
    }

    if ($PromptNow) {
        $approval = Get-ImmediateApproval -Request $request
        if ($approval.Available -and $approval.Approved) {
            $component = [pscustomobject]@{
                id = $request.id
                displayName = $request.displayName
                executablePath = $request.executablePath
                arguments = $request.arguments
                sha256 = $request.sha256
                approvedAt = (Get-Date).ToUniversalTime().ToString('o')
            }
            $approved.components = @($approved.components | Where-Object { -not [string]::Equals($_.executablePath, $resolvedPath, [System.StringComparison]::OrdinalIgnoreCase) }) + @($component)
            Save-ApprovedComponents -Document $approved
            if ($isUpdate) {
                Write-ComponentBridgeLog "REAPPROVED_IMMEDIATELY id=$($request.id) name=$DisplayName"
                Write-Output 'The component update was approved. No running process was changed.'
            }
            else {
                Write-ComponentBridgeLog "APPROVED_IMMEDIATELY id=$($request.id) name=$DisplayName"
                Write-Output 'The component was approved. It will receive the scoped proxy when the bridge starts it; no running process was changed.'
            }
            return
        }
        if ($approval.Available) {
            Write-ComponentBridgeLog "REQUEST_DECLINED id=$($request.id) name=$DisplayName"
            Write-Output 'The component was not added. No settings or running processes were changed.'
            return
        }
    }

    $pending.requests = @($pending.requests) + @($request)
    Save-PendingComponentRequests -Document $pending
    Write-ComponentBridgeLog "REQUESTED id=$($request.id) name=$DisplayName"
    if ($PromptNow) {
        Write-Output 'The request was queued because the Windows approval prompt was unavailable. It cannot receive the proxy or start until the next bridge launch asks for approval.'
    }
    else {
        Write-Output 'The request was queued. It cannot receive the proxy or start until the next bridge launch asks for your approval.'
    }
}
catch {
    Write-ComponentBridgeLog "REQUEST_FAILED error=$($_.Exception.Message)"
    Write-Error $_
    exit 1
}
