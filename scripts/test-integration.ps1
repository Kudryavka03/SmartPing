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

    # Paused targets on a synthetic Agent keep average regressions independent of live probe timing.
    $averageNodeId = 'integration-average-fixture'
    $emptyAverageNodeId = 'integration-average-empty'
    foreach ($fixtureNodeId in @($averageNodeId, $emptyAverageNodeId)) {
        $registration = @{ id = $fixtureNodeId; name = $fixtureNodeId; address = '127.0.0.1' }
        Assert-Check ((Invoke-Api '/api/agent/register' 'POST' $registration $agentToken).StatusCode -eq 200) `
            "Average fixture Agent $fixtureNodeId registers"
    }
    $averageTargets = @{}
    foreach ($fixture in @(
        @{ key = 'mixed'; protocol = 'tcp'; port = 443 },
        @{ key = 'suspectOnly'; protocol = 'tcp'; port = 444 },
        @{ key = 'icmp'; protocol = 'icmp'; port = 443 }
    )) {
        $request = @{
            name = "Average fixture $($fixture.key)"; host = '127.0.0.1'; protocol = $fixture.protocol
            port = $fixture.port; intervalSeconds = 3600; timeoutMs = 2000; retransmitThresholdMs = 100
            nodeIds = @($averageNodeId); group = 'Average regression'; enabled = $false
        }
        $response = Invoke-Api '/api/targets' 'POST' $request
        Assert-Check ($response.StatusCode -eq 201 -and -not $response.Body.enabled) `
            "Paused $($fixture.key) average fixture is created"
        $averageTargets[$fixture.key] = $response.Body
    }
    $averageTimestamp = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o')
    $averageSamples = @(
        @{ targetId = $averageTargets.mixed.id; latencyMs = 10; success = $true },
        @{ targetId = $averageTargets.mixed.id; latencyMs = 30; success = $true },
        @{ targetId = $averageTargets.mixed.id; latencyMs = 500; success = $true },
        @{ targetId = $averageTargets.mixed.id; latencyMs = $null; success = $false },
        @{ targetId = $averageTargets.suspectOnly.id; latencyMs = 500; success = $true },
        @{ targetId = $averageTargets.icmp.id; latencyMs = 100; success = $true },
        @{ targetId = $averageTargets.icmp.id; latencyMs = 100; success = $true }
    )
    $averageSequence = 0
    foreach ($sample in $averageSamples) {
        # Samples share a chart bucket; deterministic IDs also preserve their probe order for jitter.
        $sample.id = ('average-{0:D2}-' -f $averageSequence) + [Guid]::NewGuid().ToString('N')
        $averageSequence++
        $sample.nodeId = $averageNodeId
        $sample.timestamp = $averageTimestamp
        $sample.suspectedRetransmit = $false
    }
    $averageReport = Invoke-Api '/api/agent/report' 'POST' @{ nodeId = $averageNodeId; samples = $averageSamples } $agentToken
    Assert-Check ($averageReport.StatusCode -eq 200 -and $averageReport.Body.accepted -eq 7) `
        'All seven deterministic average fixture samples are accepted'

    $mixedHistory = Get-History $averageTargets.mixed.id
    Assert-Check ($mixedHistory.stats.avgLatencyMs -eq 20 -and $mixedHistory.stats.avgLatencySampleCount -eq 2) `
        'TCP average excludes suspected retransmits and exposes the two eligible samples'
    Assert-Check ($mixedHistory.stats.jitterMs -eq 20) `
        'TCP jitter uses the adjacent normal replies and excludes the subsequent suspected retransmit'
    Assert-Check ($mixedHistory.stats.sent -eq 4 -and $mixedHistory.stats.received -eq 3 -and
        $mixedHistory.stats.lossPercent -eq 25 -and $mixedHistory.stats.retransmitPercent -eq 25) `
        'Excluding retransmits from TCP average preserves sent, received, loss and retransmit counts'
    Assert-Check ($mixedHistory.stats.minLatencyMs -eq 10 -and $mixedHistory.stats.maxLatencyMs -eq 500 -and
        @($mixedHistory.recent | Where-Object { $_.latencyMs -eq 500 -and $_.success -and $_.suspectedRetransmit }).Count -eq 1) `
        'TCP extrema and raw successful 500 ms retransmit remain visible'
    $mixedSeries = @($mixedHistory.series)
    Assert-Check ($mixedSeries.Count -eq 1 -and $mixedSeries[0].latencyMs -eq 20 -and
        $mixedSeries[0].sent -eq 4 -and $mixedSeries[0].lossPercent -eq 25 -and
        $mixedSeries[0].retransmitPercent -eq 25 -and $mixedSeries[0].maxLatencyMs -eq 500) `
        'TCP chart bucket average excludes retransmits while preserving loss, retransmits and extrema'

    $suspectHistory = Get-History $averageTargets.suspectOnly.id
    Assert-Check ($null -eq $suspectHistory.stats.jitterMs) `
        'Retransmit-only TCP history has no valid jitter pair'
    Assert-Check ($null -eq $suspectHistory.stats.avgLatencyMs -and $suspectHistory.stats.avgLatencySampleCount -eq 0 -and
        $suspectHistory.stats.sent -eq 1 -and $suspectHistory.stats.received -eq 1 -and
        $suspectHistory.stats.lossPercent -eq 0 -and $suspectHistory.stats.retransmitPercent -eq 100) `
        'TCP history containing only a successful retransmit has no average and no packet loss'
    $suspectSeries = @($suspectHistory.series)
    Assert-Check ($suspectSeries.Count -eq 1 -and $null -eq $suspectSeries[0].latencyMs -and
        $suspectSeries[0].minLatencyMs -eq 500 -and $suspectSeries[0].maxLatencyMs -eq 500 -and
        $suspectHistory.recent[0].success -and $suspectHistory.recent[0].suspectedRetransmit -and
        $suspectHistory.recent[0].latencyMs -eq 500) `
        'Retransmit-only chart bucket has a null average while raw latency and extrema remain'

    $icmpAverageHistory = Get-History $averageTargets.icmp.id
    Assert-Check ($icmpAverageHistory.stats.jitterMs -eq 0) `
        'Equal adjacent ICMP replies preserve a genuine zero jitter'
    Assert-Check ($icmpAverageHistory.stats.avgLatencyMs -eq 100 -and $icmpAverageHistory.stats.avgLatencySampleCount -eq 2 -and
        $icmpAverageHistory.stats.received -eq 2 -and $icmpAverageHistory.stats.retransmitPercent -eq 0 -and
        @($icmpAverageHistory.series).Count -eq 1 -and $icmpAverageHistory.series[0].latencyMs -eq 100) `
        'ICMP history and chart bucket retain every successful sample in the average'

    $averageDashboard = (Invoke-Api "/api/dashboard?nodeId=$averageNodeId").Body
    Assert-Check ($averageDashboard.summary.avgLatencyMs -eq 60 -and $averageDashboard.summary.sent -eq 7 -and
        [Math]::Abs($averageDashboard.summary.lossPercent - 100.0 / 7) -lt 0.000001) `
        'Dashboard average weights TCP and ICMP targets by eligible sample count rather than received count'
    $mixedCard = @($averageDashboard.targets | Where-Object id -eq $averageTargets.mixed.id)[0]
    Assert-Check ($mixedCard.stats.avgLatencyMs -eq 20 -and $mixedCard.stats.avgLatencySampleCount -eq 2 -and
        $mixedCard.series[0].latencyMs -eq 20) `
        'Dashboard target card exposes the same eligible count and average as history'
    Assert-Check ($mixedCard.stats.jitterMs -eq 20) `
        'Link performance dashboard exposes the corrected TCP average jitter'
    $otherNodeHistory = (Invoke-Api "/api/targets/$($averageTargets.mixed.id)/history?nodeId=$emptyAverageNodeId").Body
    Assert-Check ($null -eq $otherNodeHistory.stats.avgLatencyMs -and $otherNodeHistory.stats.avgLatencySampleCount -eq 0 -and
        $otherNodeHistory.stats.sent -eq 0 -and @($otherNodeHistory.series).Count -eq 0) `
        'History filtered to another node excludes the fixture samples and returns no average'
    foreach ($emptyNodeFilter in @($emptyAverageNodeId, 'integration-average-unknown')) {
        $emptyDashboard = (Invoke-Api "/api/dashboard?nodeId=$emptyNodeFilter").Body
        Assert-Check ($null -eq $emptyDashboard.summary.avgLatencyMs -and $emptyDashboard.summary.sent -eq 0 -and
            $emptyDashboard.summary.lossPercent -eq 0) `
            "Dashboard node filter $emptyNodeFilter has no average when no eligible samples exist"
    }
    foreach ($key in @('mixed', 'icmp')) {
        Assert-Check ((Invoke-Api "/api/targets/$($averageTargets[$key].id)" 'DELETE').StatusCode -eq 200) `
            "Average fixture $key is removed"
    }
    $onlySuspectDashboard = (Invoke-Api "/api/dashboard?nodeId=$averageNodeId").Body
    $onlySuspectCard = @($onlySuspectDashboard.targets | Where-Object id -eq $averageTargets.suspectOnly.id)[0]
    Assert-Check ($null -eq $onlySuspectDashboard.summary.avgLatencyMs -and $onlySuspectDashboard.summary.sent -eq 1 -and
        $onlySuspectDashboard.summary.lossPercent -eq 0 -and $onlySuspectCard.stats.received -eq 1 -and
        $onlySuspectCard.stats.retransmitPercent -eq 100 -and $onlySuspectCard.stats.avgLatencySampleCount -eq 0) `
        'Dashboard containing only successful retransmits shows no average without treating them as loss'
    Assert-Check ((Invoke-Api "/api/targets/$($averageTargets.suspectOnly.id)" 'DELETE').StatusCode -eq 200) `
        'Retransmit-only average fixture is removed'
    foreach ($fixtureNodeId in @($averageNodeId, $emptyAverageNodeId)) {
        Assert-Check ((Invoke-Api "/api/nodes/$fixtureNodeId" 'DELETE').StatusCode -eq 200) `
            "Average fixture Agent $fixtureNodeId is removed"
    }

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
