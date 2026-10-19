[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Name = 'SmartPing',
    [ValidateSet('server', 'agent')][string]$Mode = 'server',
    [string]$ListenAddress = '127.0.0.1',
    [ValidateRange(1, 65535)][int]$Port = 5080,
    [string]$ServerUrl = 'http://127.0.0.1:5080',
    [string]$NodeName = $env:COMPUTERNAME,
    [string]$NodeAddress = '',
    [string]$NodeId = '',
    [string]$AgentToken = $env:SmartPing__AgentToken,
    [string]$AdminToken = $env:SmartPing__AdminToken,
    [string]$DataDirectory = '',
    [ValidateRange(1, 3650)][int]$RetentionDays = 30
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This script requires Windows.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator to install a Windows service.'
}
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$exePath = Join-Path $PublishDirectory 'SmartPing.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "SmartPing.exe was not found in $PublishDirectory. Run publish.ps1 first."
}
if (Get-Service -Name $Name -ErrorAction SilentlyContinue) { throw "Service $Name already exists." }
$configPath = Join-Path $PublishDirectory 'appsettings.Production.json'
if (Test-Path -LiteralPath $configPath) {
    throw 'appsettings.Production.json already exists. Use a separate publish directory for each service instance.'
}
if ($Mode -eq 'agent' -and [string]::IsNullOrWhiteSpace($AgentToken)) {
    throw 'Agent mode requires -AgentToken or SmartPing__AgentToken.'
}
$loopbackAddresses = @('127.0.0.1', 'localhost', '::1', '[::1]')
if ($Mode -eq 'server' -and $ListenAddress -notin $loopbackAddresses -and
    ([string]::IsNullOrWhiteSpace($AdminToken) -or $AdminToken.Length -lt 16 -or
        [string]::IsNullOrWhiteSpace($AgentToken) -or $AgentToken.Length -lt 16)) {
    throw 'A non-loopback server requires both -AdminToken and -AgentToken, at least 16 characters each.'
}
if ([string]::IsNullOrWhiteSpace($DataDirectory)) { $DataDirectory = Join-Path $PublishDirectory 'data' }
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$urlHost = $ListenAddress
if ($urlHost.Contains(':') -and -not $urlHost.StartsWith('[')) { $urlHost = '[' + $urlHost + ']' }
$configuration = @{
    Urls = "http://${urlHost}:$Port"
    ServiceName = $Name
    Logging = @{
        EventLog = @{ SourceName = $Name; LogName = 'Application' }
    }
    SmartPing = @{
        Mode = $Mode
        DataDirectory = $DataDirectory
        RetentionDays = $RetentionDays
        NodeName = $NodeName
        NodeAddress = $NodeAddress
        NodeId = $NodeId
        ServerUrl = $ServerUrl
        AgentToken = $AgentToken
        AdminToken = $AdminToken
    }
}

if ($PSCmdlet.ShouldProcess($Name, 'Create protected configuration and install a stopped Windows service')) {
    # Register while elevated: LocalService cannot create an event source on first start.
    if (-not [Diagnostics.EventLog]::SourceExists($Name)) {
        [Diagnostics.EventLog]::CreateEventSource($Name, 'Application')
    }
    elseif ([Diagnostics.EventLog]::LogNameFromSourceName($Name, '.') -ne 'Application') {
        throw "Event log source $Name already belongs to another log. Choose a different service name."
    }
    # Administrators and SYSTEM control the configuration; LocalService can only read it.
    $adminSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $serviceSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-19')
    $fileAcl = New-Object Security.AccessControl.FileSecurity
    $fileAcl.SetAccessRuleProtection($true, $false)
    $fileAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($adminSid, 'FullControl', 'Allow')))
    $fileAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, 'FullControl', 'Allow')))
    $fileAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($serviceSid, 'Read', 'Allow')))
    [IO.File]::WriteAllText($configPath, '')
    Set-Acl -LiteralPath $configPath -AclObject $fileAcl
    [IO.File]::WriteAllText($configPath, ($configuration | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))

    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    $dataAcl = Get-Acl -LiteralPath $DataDirectory
    $dataAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($serviceSid, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $DataDirectory -AclObject $dataAcl

    # Publish directories must permit LocalService to read the executable and static assets.
    $publishAcl = Get-Acl -LiteralPath $PublishDirectory
    $publishAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($serviceSid, 'ReadAndExecute', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $PublishDirectory -AclObject $publishAcl

    $binaryPath = '"' + $exePath + '" --environment Production'
    $emptyPassword = New-Object Security.SecureString
    $credential = New-Object Management.Automation.PSCredential('NT AUTHORITY\LocalService', $emptyPassword)
    New-Service -Name $Name -DisplayName $Name -BinaryPathName $binaryPath `
        -StartupType Automatic -Credential $credential `
        -Description "SmartPing $Mode network latency monitor" | Out-Null
    Write-Host "Installed $Name as LocalService. The service has not been started."
    Write-Host "Start it with: Start-Service -Name '$Name'"
    Write-Host "Configuration: $configPath"
}
