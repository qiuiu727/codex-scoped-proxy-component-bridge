[CmdletBinding(DefaultParameterSetName = 'List')]
param(
    [Parameter(ParameterSetName = 'Remove', Mandatory = $true)][string]$ComponentId,
    [Parameter(ParameterSetName = 'Remove', Mandatory = $true)][switch]$Remove,
    [Parameter(ParameterSetName = 'List')][switch]$List
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CodexScopedProxyComponents.psm1') -Force

try {
    $registry = Get-ApprovedComponents
    if ($PSCmdlet.ParameterSetName -eq 'List') {
        @($registry.components) | Select-Object id,displayName,executablePath,arguments,approvedAt | Format-Table -AutoSize
        exit 0
    }

    $target = @($registry.components | Where-Object { $_.id -eq $ComponentId }) | Select-Object -First 1
    if (-not $target) {
        throw "No approved component has id $ComponentId"
    }
    $prompt = "Remove '$($target.displayName)' from the approved proxy-component list? Type REMOVE to continue"
    $answer = Read-Host $prompt
    if ($answer -cne 'REMOVE') {
        Write-Output 'No change was made.'
        exit 0
    }
    $registry.components = @($registry.components | Where-Object { $_.id -ne $ComponentId })
    Save-ApprovedComponents -Document $registry
    Write-ComponentBridgeLog "REMOVED id=$ComponentId name=$($target.displayName)"
    Write-Output 'The component was removed. It will not be started by this bridge again.'
}
catch {
    Write-ComponentBridgeLog "MANAGE_FAILED error=$($_.Exception.Message)"
    Write-Error $_
    exit 1
}
