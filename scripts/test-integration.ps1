#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src/SmartPing/SmartPing.csproj'
$contentDirectory = Split-Path -Parent $projectPath
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryName = 'SmartPingIntegration-' + [Guid]::NewGuid().ToString('N')
$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot $temporaryName))
$testBuildDirectory = Join-Path $temporaryDirectory 'build'
$assemblyPath = Join-Path $testBuildDirectory 'SmartPing.dll'
$processes = [Collections.Generic.List[object]]::new()
$adminToken = [Guid]::NewGuid().ToString('N')
$agentToken = [Guid]::NewGuid().ToString('N')
$checkCount = 0
$completed = $false

function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $script:checkCount++
    Write-Host "PASS $Description"
}

function Start-Monitor([string]$Label, [string[]]$Arguments, [bool]$IncludeTokens = $true) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnetPath
    $startInfo.WorkingDirectory = $contentDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($assemblyPath)
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    # Keep the test independent of any monitoring configuration in the parent shell.
    foreach ($key in @($startInfo.Environment.Keys)) {
        if ($key -like 'SmartPing__*' -or $key -like 'ASPNETCORE_*' -or $key -eq 'DOTNET_ENVIRONMENT') {
            $startInfo.Environment.Remove($key) | Out-Null
        }
    }
    $startInfo.Environment['DOTNET_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['SmartPing__AdminToken'] = $(if ($IncludeTokens) { $adminToken } else { '' })
    $startInfo.Environment['SmartPing__AgentToken'] = $(if ($IncludeTokens) { $agentToken } else { '' })
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start $Label." }
    $entry = [PSCustomObject]@{
        Label = $Label
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        Error = $process.StandardError.ReadToEndAsync()
    }
    $processes.Add($entry)
    return $entry
}

function Stop-Monitor($Entry) {
    if (-not $Entry.Process.HasExited) {
        $Entry.Process.Kill($true)
        if (-not $Entry.Process.WaitForExit(5000)) { throw "Failed to stop $($Entry.Label)." }
    }
}

function Invoke-Api([string]$Path, [string]$Method = 'GET', $Body = $null, [string]$Token = $adminToken) {
    $parameters = @{
        Uri = $baseUrl + $Path
        Method = $Method
        SkipHttpErrorCheck = $true
        StatusCodeVariable = 'statusCode'
        TimeoutSec = 4
    }
    if (-not [string]::IsNullOrEmpty($Token)) { $parameters.Headers = @{ Authorization = "Bearer $Token" } }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress))
    }
    $payload = Invoke-RestMethod @parameters
    return [PSCustomObject]@{ StatusCode = [int]$statusCode; Body = $payload }
}

function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$Seconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Milliseconds 400
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}

function Get-History([string]$TargetId) {
    $response = Invoke-Api "/api/targets/$TargetId/history?hours=1"
    if ($response.StatusCode -ne 200) { throw "History returned $($response.StatusCode)." }
    return $response.Body
}

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    $buildOutput = & $dotnetPath build $projectPath -c Release --nologo -o $testBuildDirectory -p:UseAppHost=false 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($buildOutput -join [Environment]::NewLine) }
    Assert-Check $true 'Release build succeeds'

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"
    $hostname = [Net.Dns]::GetHostName()

    $rejected = Start-Monitor 'unauthenticated external listener' @(
        "--urls=http://0.0.0.0:$port", '--SmartPing:Mode=server',
        "--SmartPing:DataDirectory=$(Join-Path $temporaryDirectory 'rejected')") $false
    $rejectedExited = $rejected.Process.WaitForExit(5000)
    if (-not $rejectedExited) { Stop-Monitor $rejected }
    $rejectedLog = $rejected.Output.GetAwaiter().GetResult() + $rejected.Error.GetAwaiter().GetResult()
    Assert-Check ($rejectedExited -and $rejected.Process.ExitCode -ne 0 -and $rejectedLog -match 'Remote listening requires') `
        'External binding without both tokens is rejected before startup'

    $centerArguments = @(
        "--urls=$baseUrl", '--SmartPing:Mode=server',
        "--SmartPing:DataDirectory=$(Join-Path $temporaryDirectory 'center')",
        '--SmartPing:NodeName=Integration center', "--SmartPing:NodeAddress=$hostname")
    $center = Start-Monitor 'center' $centerArguments
    Wait-Until { (Invoke-Api '/api/health' -Token '').StatusCode -eq 200 } 'center health' 20
    Assert-Check $true 'Center starts and public health responds'
    Assert-Check ((Invoke-Api '/api/nodes' -Token '').StatusCode -eq 401) 'Missing admin token returns 401'
    Assert-Check ((Invoke-Api '/api/nodes' -Token $agentToken).StatusCode -eq 401) 'Agent token cannot access admin API'
    $wrongRegistration = @{ id = 'wrong-token'; name = 'Wrong token'; address = $hostname }
    Assert-Check ((Invoke-Api '/api/agent/register' 'POST' $wrongRegistration $adminToken).StatusCode -eq 401) `
        'Admin token cannot register an Agent'
    Assert-Check ((Invoke-Api '/api/agent/register' 'POST' $wrongRegistration '').StatusCode -eq 401) `
        'Agent registration requires its bearer token'
    $invalid = @{ name = 'Invalid'; host = '127.0.0.1'; protocol = 'tcp'; port = $port; timeoutMs = 100; retransmitThresholdMs = 100; nodeIds = @('local') }
    Assert-Check ((Invoke-Api '/api/targets' 'POST' $invalid).StatusCode -eq 400) 'Invalid TCP threshold is rejected with 400'

    foreach ($nodeId in @('integration-agent-1', 'integration-agent-2')) {
        Start-Monitor $nodeId @(
            '--SmartPing:Mode=agent', "--SmartPing:ServerUrl=$baseUrl",
            "--SmartPing:NodeId=$nodeId", "--SmartPing:NodeName=$nodeId",
            "--SmartPing:NodeAddress=$hostname",
            "--SmartPing:DataDirectory=$(Join-Path $temporaryDirectory $nodeId)") | Out-Null
    }
    Wait-Until { @((Invoke-Api '/api/nodes').Body).Count -eq 3 } 'both agents register' 30
    Assert-Check $true 'Two outbound-only Agents register alongside the center'

    $icmpRequest = @{ name = 'Integration ICMP'; host = '127.0.0.1'; protocol = 'icmp'; packetSize = 128; intervalSeconds = 1; nodeIds = @('local'); group = 'Integration'; enabled = $true }
    $icmpResponse = Invoke-Api '/api/targets' 'POST' $icmpRequest
    $icmp = $icmpResponse.Body
    Assert-Check ($icmpResponse.StatusCode -eq 201 -and $icmp.packetSize -eq 128) 'ICMP target accepts and preserves 128-byte payload'
    Wait-Until { (Get-History $icmp.id).stats.received -gt 0 } 'successful local ICMP sample' 15
    Assert-Check $true 'Local ICMP probe reaches loopback successfully'

    $tcpRequest = @{ name = 'Integration TCP'; host = '127.0.0.1'; protocol = 'tcp'; port = $port; intervalSeconds = 1; timeoutMs = 2000; retransmitThresholdMs = 1; nodeIds = @('local', 'integration-agent-1', 'integration-agent-2'); group = 'Integration'; enabled = $true }
    $tcpResponse = Invoke-Api '/api/targets' 'POST' $tcpRequest
    $tcp = $tcpResponse.Body
    Assert-Check ($tcpResponse.StatusCode -eq 201) 'TCP target is assigned to all three nodes'
    Wait-Until {
        $successfulNodes = @((Get-History $tcp.id).recent | Where-Object success | Select-Object -ExpandProperty nodeId -Unique)
        $successfulNodes.Count -eq 3
    } 'TCP samples from the center and both agents' 30
    Assert-Check $true 'TCP samples arrive successfully from all three nodes'

    $invalidSampleId = [Guid]::NewGuid().ToString('N')
    $malformedReport = @{
        nodeId = 'integration-agent-1'
        samples = @(@{ id = $invalidSampleId; targetId = $tcp.id; nodeId = 'integration-agent-2'; timestamp = [DateTimeOffset]::UtcNow.ToString('o'); latencyMs = 1; success = $true; suspectedRetransmit = $false })
    }
    $reported = Invoke-Api '/api/agent/report' 'POST' $malformedReport $agentToken
    Assert-Check ($reported.StatusCode -eq 200 -and $reported.Body.accepted -eq 0 -and
        @((Get-History $tcp.id).recent | Where-Object id -eq $invalidSampleId).Count -eq 0) `
        'Report samples claiming a different node are filtered out'

    $futureSampleId = [Guid]::NewGuid().ToString('N')
    $futureReport = @{
        nodeId = 'integration-agent-1'
        samples = @(@{ id = $futureSampleId; targetId = $tcp.id; nodeId = 'integration-agent-1'; timestamp = [DateTimeOffset]::UtcNow.AddMinutes(2).ToString('o'); latencyMs = 1; success = $true; suspectedRetransmit = $false })
    }
    $futureResponse = Invoke-Api '/api/agent/report' 'POST' $futureReport $agentToken
    $futureSample = @((Get-History $tcp.id).recent | Where-Object id -eq $futureSampleId)
    Assert-Check ($futureResponse.StatusCode -eq 200 -and $futureResponse.Body.accepted -eq 1 -and
        $futureSample.Count -eq 1 -and [DateTimeOffset]$futureSample[0].timestamp -le [DateTimeOffset]::UtcNow) `
        'Small Agent clock skew is clamped to server time and remains visible in history'

    $tcp.enabled = $false
    Assert-Check ((Invoke-Api "/api/targets/$($tcp.id)" 'PUT' $tcp).StatusCode -eq 200) 'Project can be paused by update'
    # One configuration/report cycle plus margin lets agents flush in-flight samples.
    Start-Sleep -Seconds 12
    $pausedCount = (Get-History $tcp.id).stats.sent
    Start-Sleep -Seconds 3
    Assert-Check ((Get-History $tcp.id).stats.sent -eq $pausedCount) 'Paused project stops generating samples after configuration propagation'
    $tcp.enabled = $true
    $resumeAt = [DateTimeOffset]::UtcNow
    Invoke-Api "/api/targets/$($tcp.id)" 'PUT' $tcp | Out-Null
    Wait-Until {
        $resumedNodes = @((Get-History $tcp.id).recent | Where-Object {
            $_.success -and [DateTimeOffset]$_.timestamp -gt $resumeAt
        } | Select-Object -ExpandProperty nodeId -Unique)
        $resumedNodes.Count -eq 3
    } 'resumed probes from all three nodes' 30
    Assert-Check $true 'Resumed project collects fresh samples from all three nodes'

    $beforeRestart = (Get-History $tcp.id).stats.sent
    $outageStart = [DateTimeOffset]::UtcNow
    Stop-Monitor $center
    Start-Sleep -Seconds 4
    $center = Start-Monitor 'restarted center' $centerArguments
    Wait-Until { (Invoke-Api '/api/health' -Token '').StatusCode -eq 200 } 'restarted center health' 20
    $outageEnd = [DateTimeOffset]::UtcNow
    $persistedTargets = @((Invoke-Api '/api/targets').Body)
    Assert-Check ($persistedTargets.Count -eq 2 -and (Get-History $tcp.id).stats.sent -ge $beforeRestart) `
        'Center restart preserves target configuration and history'
    Wait-Until {
        @((Get-History $tcp.id).recent | Where-Object {
            $_.nodeId -like 'integration-agent-*' -and [DateTimeOffset]$_.timestamp -gt $outageStart -and [DateTimeOffset]$_.timestamp -lt $outageEnd
        }).Count -gt 0
    } 'buffered Agent samples after center restart' 30
    Assert-Check $true 'Agents buffer outage samples and flush them after reconnection'

    $meshRequest = @{ nodeIds = @('local', 'integration-agent-1', 'integration-agent-2'); protocol = 'icmp'; packetSize = 32; intervalSeconds = 3600 }
    $meshResponse = Invoke-Api '/api/mesh' 'POST' $meshRequest
    $meshTargets = @((Invoke-Api '/api/targets').Body | Where-Object { $_.id -notin @($icmp.id, $tcp.id) })
    foreach ($target in $meshTargets) {
        $target.enabled = $false
        Invoke-Api "/api/targets/$($target.id)" 'PUT' $target | Out-Null
    }
    Assert-Check ($meshResponse.StatusCode -eq 200 -and $meshResponse.Body.created -eq 3 -and $meshTargets.Count -eq 3) `
        'Mesh links with the same hostname are deduplicated to one target per source node'
    $duplicateMesh = Invoke-Api '/api/mesh' 'POST' $meshRequest
    Assert-Check ($duplicateMesh.StatusCode -eq 200 -and $duplicateMesh.Body.created -eq 0) 'Repeated mesh creation skips existing links'
    foreach ($target in $meshTargets) { Invoke-Api "/api/targets/$($target.id)" 'DELETE' | Out-Null }

    $deleteTcp = Invoke-Api "/api/targets/$($tcp.id)" 'DELETE'
    Assert-Check ($deleteTcp.StatusCode -eq 200 -and (Invoke-Api "/api/targets/$($tcp.id)/history").StatusCode -eq 404) `
        'Deleted project and its history are no longer accessible'
    Invoke-Api "/api/targets/$($icmp.id)" 'DELETE' | Out-Null
    Assert-Check (@((Invoke-Api '/api/targets').Body | Where-Object { $null -ne $_ }).Count -eq 0) 'All isolated test projects are removed'
    $completed = $true
    Write-Host "Integration passed: $checkCount checks."
}
finally {
    foreach ($entry in $processes) {
        try { Stop-Monitor $entry } catch { Write-Warning $_ }
    }
    if (-not $completed) {
        foreach ($entry in $processes) {
            if ($entry.Process.HasExited) {
                Write-Host "Process log: $($entry.Label)"
                ($entry.Output.GetAwaiter().GetResult() + $entry.Error.GetAwaiter().GetResult()) -split '\r?\n' | Select-Object -Last 15 | Write-Host
            }
        }
    }
    foreach ($entry in $processes) { $entry.Process.Dispose() }
    # Validate the exact directory created by this invocation before recursive removal.
    $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
    $expectedParent = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ((Split-Path -Parent $resolvedTemporaryDirectory) -ne $expectedParent -or
        (Split-Path -Leaf $resolvedTemporaryDirectory) -ne $temporaryName -or
        -not $temporaryName.StartsWith('SmartPingIntegration-')) {
        throw "Refusing to remove unexpected test path: $resolvedTemporaryDirectory"
    }
    if (Test-Path -LiteralPath $resolvedTemporaryDirectory) {
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }
}
