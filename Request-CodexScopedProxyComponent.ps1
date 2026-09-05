[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$Arguments = '',
    [string]$DisplayName
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CodexScopedProxyComponents.psm1') -Force

try {
    $resolvedPath = Resolve-ComponentExecutablePath -Path $ExecutablePath
    if ([string]::IsNullOrWhiteSpace($DisplayName)) {
        $DisplayName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedPath)
    }

    $pending = Get-PendingComponentRequests
    $approved = Get-ApprovedComponents
    if (@($approved.components | Where-Object { [string]::Equals($_.executablePath, $resolvedPath, [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        throw 'This executable is already approved. Use the approved-components list to remove it before requesting it again.'
    }
    if (@($pending.requests | Where-Object { [string]::Equals($_.executablePath, $resolvedPath, [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        throw 'A request for this executable is already pending approval.'
    }

    $request = [pscustomobject]@{
        id = [guid]::NewGuid().Guid
        displayName = $DisplayName
        executablePath = $resolvedPath
        arguments = $Arguments
        sha256 = Get-ComponentSha256 -Path $resolvedPath
        requestedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    $pending.requests = @($pending.requests) + @($request)
    Save-PendingComponentRequests -Document $pending
    Write-ComponentBridgeLog "REQUESTED id=$($request.id) name=$DisplayName"
    Write-Output 'The request was queued. It cannot receive the proxy or start until the next bridge launch asks for your approval.'
}
catch {
    Write-ComponentBridgeLog "REQUEST_FAILED error=$($_.Exception.Message)"
    Write-Error $_
    exit 1
}
