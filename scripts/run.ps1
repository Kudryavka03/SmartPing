[CmdletBinding()]
param(
    [ValidateSet('server', 'agent')][string]$Mode = 'server',
    [string]$ListenAddress = '127.0.0.1',
    [ValidateRange(1, 65535)][int]$Port = 5080,
    [string]$AdminToken = $env:SmartPing__AdminToken,
    [string]$AgentToken = $env:SmartPing__AgentToken,
    [string]$NodeName = $env:COMPUTERNAME,
    [string]$NodeAddress = '',
    [string]$NodeId = '',
    [string]$ServerUrl = 'http://127.0.0.1:5080',
    [string]$DataDirectory = '',
    [ValidateRange(1, 3650)][int]$RetentionDays = 30,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src/SmartPing/SmartPing.csproj'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install the .NET 8 SDK, then run this script again.'
}
if ($Mode -eq 'agent' -and [string]::IsNullOrWhiteSpace($AgentToken)) {
    throw 'Agent mode requires -AgentToken or the SmartPing__AgentToken environment variable.'
}
$loopbackAddresses = @('127.0.0.1', 'localhost', '::1', '[::1]')
if ($Mode -eq 'server' -and $ListenAddress -notin $loopbackAddresses -and
    ([string]::IsNullOrWhiteSpace($AdminToken) -or $AdminToken.Length -lt 16 -or
        [string]::IsNullOrWhiteSpace($AgentToken) -or $AgentToken.Length -lt 16)) {
    throw 'A non-loopback server requires both -AdminToken and -AgentToken, at least 16 characters each.'
}
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path $projectRoot ('data/' + $Mode)
}
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$urlHost = $ListenAddress
if ($urlHost.Contains(':') -and -not $urlHost.StartsWith('[')) { $urlHost = '[' + $urlHost + ']' }

$arguments = @('run', '--project', $projectPath, '--no-launch-profile')
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--', "--urls=http://${urlHost}:$Port", "--SmartPing:Mode=$Mode",
    "--SmartPing:DataDirectory=$DataDirectory", "--SmartPing:RetentionDays=$RetentionDays",
    "--SmartPing:ServerUrl=$ServerUrl", "--SmartPing:NodeAddress=$NodeAddress")
if (-not [string]::IsNullOrWhiteSpace($NodeName)) { $arguments += "--SmartPing:NodeName=$NodeName" }
if (-not [string]::IsNullOrWhiteSpace($NodeId)) { $arguments += "--SmartPing:NodeId=$NodeId" }

# Tokens stay out of the child process command line and are restored when it exits.
$previousAdminToken = $env:SmartPing__AdminToken
$previousAgentToken = $env:SmartPing__AgentToken
try {
    $env:SmartPing__AdminToken = $AdminToken
    $env:SmartPing__AgentToken = $AgentToken
    & dotnet @arguments
    $resultCode = $LASTEXITCODE
}
finally {
    $env:SmartPing__AdminToken = $previousAdminToken
    $env:SmartPing__AgentToken = $previousAgentToken
}
if ($resultCode -ne 0) { throw "SmartPing exited with code $resultCode." }
