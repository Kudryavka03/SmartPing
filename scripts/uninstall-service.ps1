[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Name = 'SmartPing'
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This script requires Windows.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator to uninstall a Windows service.'
}
$service = Get-Service -Name $Name -ErrorAction Stop
if ($PSCmdlet.ShouldProcess($Name, 'Stop and remove the Windows service; keep all files and data')) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $Name -ErrorAction Stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $Name
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with code $LASTEXITCODE." }
    Write-Host 'Service removed. Published files, configuration, and monitoring data were preserved.'
}
