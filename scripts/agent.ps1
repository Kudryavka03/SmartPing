[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [string]$Token = $env:SmartPing__AgentToken,
    [string]$Name = $env:COMPUTERNAME,
    [string]$Address = '',
    [string]$NodeId = '',
    [string]$DataDirectory = '',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Token)) {
    throw 'Provide -Token or set the SmartPing__AgentToken environment variable.'
}
$parameters = @{
    Mode = 'agent'
    ServerUrl = $ServerUrl
    AgentToken = $Token
    NodeName = $Name
    NodeAddress = $Address
    NodeId = $NodeId
    DataDirectory = $DataDirectory
    NoBuild = $NoBuild
}
& (Join-Path $PSScriptRoot 'run.ps1') @parameters
