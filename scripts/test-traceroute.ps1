#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src/SmartPing/SmartPing.csproj'
$contentDirectory = Split-Path -Parent $projectPath
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryName = 'SmartPingTraceroute-' + [Guid]::NewGuid().ToString('N')
$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot $temporaryName))
$buildDirectory = Join-Path $temporaryDirectory 'build'
$assemblyPath = Join-Path $buildDirectory 'SmartPing.dll'
$processes = [Collections.Generic.List[object]]::new()
$adminToken = [Guid]::NewGuid().ToString('N')
$agentToken = [Guid]::NewGuid().ToString('N')
$agentId = 'trace-agent-' + [Guid]::NewGuid().ToString('N')
$checkCount = 0
$completed = $false

function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $script:checkCount++
    Write-Host "PASS $Description"
}

function Start-Monitor([string]$Label, [string[]]$Arguments, [bool]$EmptyPath = $false, [string]$Assembly = $assemblyPath) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnetPath
    $startInfo.WorkingDirectory = $contentDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($Assembly)
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    foreach ($key in @($startInfo.Environment.Keys)) {
        if ($key -like 'SmartPing__*' -or $key -like 'ASPNETCORE_*' -or $key -eq 'DOTNET_ENVIRONMENT') {
            $startInfo.Environment.Remove($key) | Out-Null
        }
    }
    $startInfo.Environment['DOTNET_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['SmartPing__AdminToken'] = $adminToken
    $startInfo.Environment['SmartPing__AgentToken'] = $agentToken
    if ($EmptyPath) { $startInfo.Environment['PATH'] = '' }
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
        TimeoutSec = 5
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
        Start-Sleep -Milliseconds 300
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}

function Wait-Trace([string]$TraceId, [string]$Description, [int]$Seconds = 30) {
    Wait-Until {
        $response = Invoke-Api "/api/traces/$TraceId"
        $response.StatusCode -eq 200 -and $response.Body.status -notin @('queued', 'running')
    } $Description $Seconds
    return (Invoke-Api "/api/traces/$TraceId").Body
}

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    Assert-Check (Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) 'tracert.exe')) 'Windows tracert.exe is available'
    $buildOutput = & $dotnetPath build $projectPath -c Release --nologo -o $buildDirectory -p:UseAppHost=false 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($buildOutput -join [Environment]::NewLine) }
    Assert-Check $true 'Isolated Release build succeeds'
    $bundledToolDirectory = Join-Path $buildDirectory 'tools/nexttrace/win-x64'
    foreach ($file in @('ntrace.exe', 'WinDivert.dll', 'WinDivert64.sys')) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $bundledToolDirectory $file)) "Backend output includes portable dependency: $file"
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"
    $centerArguments = @(
        "--urls=$baseUrl", '--SmartPing:Mode=server',
        "--SmartPing:DataDirectory=$(Join-Path $temporaryDirectory 'center')",
        '--SmartPing:NodeName=Traceroute integration center', '--SmartPing:NodeAddress=127.0.0.1')
    $center = Start-Monitor 'bundled ntrace center with empty PATH' $centerArguments $true
    Wait-Until { (Invoke-Api '/api/health' -Token '').StatusCode -eq 200 } 'center health' 20
    Assert-Check $true 'Authenticated center starts on an isolated loopback port'
    $localRequest = @{ host = '127.0.0.1'; nodeId = 'local'; maxHops = 2; timeoutMs = 100 }
    Assert-Check ((Invoke-Api '/api/traces' 'POST' $localRequest '').StatusCode -eq 401) 'Trace creation requires the admin token'
    Assert-Check ((Invoke-Api '/api/traces' 'POST' $localRequest $agentToken).StatusCode -eq 401) 'Agent token cannot create management trace jobs'
    $tool = Invoke-Api '/api/traces/tools'
    Assert-Check ($tool.StatusCode -eq 200 -and $tool.Body.available -and $tool.Body.tool -eq 'ntrace') 'Center prefers real ntrace.exe'
    Assert-Check ($tool.Body.supportsTcp -and $tool.Body.supportsPsize) 'ntrace advertises TCP and packet-size support'
    $localNode = @((Invoke-Api '/api/nodes').Body | Where-Object id -eq 'local')[0]
    Assert-Check ($localNode.traceVersion -eq 2) 'Center advertises the current trace protocol version'

    $maliciousHosts = @(
        '127.0.0.1 whoami', ' 127.0.0.1', '127.0.0.1 ', "127.0.0.1`nwhoami", "127.0.0.1`rwhoami",
        "127.0.0.1`twhoami", ('127.0.0.1' + [char]0 + 'whoami'), '127.0.0.1&whoami', '127.0.0.1|whoami',
        '127.0.0.1;whoami', '127.0.0.1>file', '127.0.0.1<file', '"127.0.0.1"', "'127.0.0.1'",
        '%TEMP%', '$env:TEMP', '`whoami`', 'http://127.0.0.1', '127.0.0.1:80', '127.0.0.1/path',
        '127.0.0.1\file', '--help', '-h', '例子.测试', 'fe80::1%12', '[::1]', '127.0.0.1(abc)'
    )
    foreach ($hostValue in $maliciousHosts) {
        $description = $hostValue | ConvertTo-Json -Compress
        $badTrace = @{ host = $hostValue; nodeId = 'local'; maxHops = 2; timeoutMs = 100 }
        $rejectedTrace = Invoke-Api '/api/traces' 'POST' $badTrace
        Assert-Check ($rejectedTrace.StatusCode -eq 400 -and [string]::IsNullOrEmpty($rejectedTrace.Body.id)) "Unsafe trace target is rejected before creating a job: $description"
        $badTarget = @{ name = 'Rejected input'; host = $hostValue; protocol = 'icmp'; packetSize = 32; intervalSeconds = 3600; nodeIds = @('local') }
        Assert-Check ((Invoke-Api '/api/targets' 'POST' $badTarget).StatusCode -eq 400) "Unsafe Ping target is rejected: $description"
    }
    foreach ($limits in @(@{ maxHops = 0; timeoutMs = 100 }, @{ maxHops = 65; timeoutMs = 100 },
        @{ maxHops = 2; timeoutMs = 99 }, @{ maxHops = 2; timeoutMs = 5001 })) {
        $badLimits = @{ host = '127.0.0.1'; nodeId = 'local'; maxHops = $limits.maxHops; timeoutMs = $limits.timeoutMs }
        Assert-Check ((Invoke-Api '/api/traces' 'POST' $badLimits).StatusCode -eq 400) "Trace parameter bounds are enforced: hops=$($limits.maxHops), timeout=$($limits.timeoutMs)"
    }
    $invalidOptions = @(
        @{ protocol = 'udp' }, @{ protocol = 'TCP' }, @{ protocol = ' tcp' }, @{ protocol = 'tcp ' },
        @{ protocol = 'tcp;whoami'; port = 443 }, @{ protocol = '' }, @{ protocol = $null },
        @{ protocol = 123 }, @{ protocol = @('tcp') },
        @{ protocol = 'icmp'; port = 443 },
        @{ protocol = 'tcp' }, @{ protocol = 'tcp'; port = $null },
        @{ protocol = 'tcp'; port = 0 }, @{ protocol = 'tcp'; port = -1 },
        @{ protocol = 'tcp'; port = 65536 }, @{ protocol = 'tcp'; port = 2147483648 },
        @{ protocol = 'tcp'; port = 443.5 }, @{ protocol = 'tcp'; port = '443' },
        @{ protocol = 'tcp'; port = '443 whoami' }, @{ protocol = 'tcp'; port = '443;whoami' },
        @{ protocol = 'tcp'; port = $true }, @{ protocol = 'tcp'; port = @(443) },
        @{ psize = 0 }, @{ psize = -1 }, @{ psize = 27 },
        @{ protocol = 'tcp'; port = 443; psize = 39 },
        @{ host = '::1'; psize = 47 }, @{ host = '::1'; protocol = 'tcp'; port = 443; psize = 59 },
        @{ psize = 65536 }, @{ psize = 2147483648 }, @{ psize = 64.5 }, @{ psize = '64' },
        @{ psize = '64 whoami' }, @{ psize = '64;whoami' }, @{ psize = ' 64' },
        @{ psize = $true }, @{ psize = @(64) }, @{ psize = @{ value = 64 } }
    )
    foreach ($invalidOption in $invalidOptions) {
        $invalidRequest = @{ host = '127.0.0.1'; nodeId = 'local'; maxHops = 2; timeoutMs = 100 }
        foreach ($key in $invalidOption.Keys) { $invalidRequest[$key] = $invalidOption[$key] }
        $description = $invalidOption | ConvertTo-Json -Depth 5 -Compress
        $invalidResponse = Invoke-Api '/api/traces' 'POST' $invalidRequest
        Assert-Check ($invalidResponse.StatusCode -eq 400 -and [string]::IsNullOrEmpty($invalidResponse.Body.id)) "Invalid protocol, port or psize is rejected before execution: $description"
    }
    Assert-Check (@((Invoke-Api '/api/targets').Body | Where-Object { $null -ne $_ }).Count -eq 0) 'Rejected inputs leave no monitoring targets behind'

    $local = Invoke-Api '/api/traces' 'POST' $localRequest
    Assert-Check ($local.StatusCode -eq 202 -and -not [string]::IsNullOrEmpty($local.Body.id)) 'Local loopback trace is accepted'
    Assert-Check ($local.Body.protocol -eq 'icmp' -and $null -eq $local.Body.port -and $null -eq $local.Body.psize) 'Omitted trace options retain ICMP and native packet-size defaults'
    $localResult = Wait-Trace $local.Body.id 'real local ntrace completes with default GeoIP enabled' 60
    Assert-Check ($localResult.status -eq 'completed' -and $localResult.tool -eq 'ntrace' -and
        $localResult.exitCode -eq 0 -and $localResult.output -match '127\.0\.0\.1') 'Real ntrace completes locally with actual loopback output'

    $agent = Start-Monitor 'bundled trace Agent with empty PATH' @(
        '--SmartPing:Mode=agent', "--SmartPing:ServerUrl=$baseUrl", "--SmartPing:NodeId=$agentId",
        '--SmartPing:NodeName=Traceroute integration Agent', '--SmartPing:NodeAddress=127.0.0.1',
        "--SmartPing:DataDirectory=$(Join-Path $temporaryDirectory 'agent')") $true
    Wait-Until { @((Invoke-Api '/api/nodes').Body | Where-Object id -eq $agentId).Count -eq 1 } 'trace Agent registration' 25
    $registeredNode = @((Invoke-Api '/api/nodes').Body | Where-Object id -eq $agentId)[0]
    Assert-Check (-not $registeredNode.isLocal -and $registeredNode.online) 'Real outbound Agent registers with its configured stable identity'
    Assert-Check ($registeredNode.traceVersion -eq 2) 'Updated real Agent advertises TCP and packet-size task compatibility'
    $remote = Invoke-Api '/api/traces' 'POST' @{ host = '127.0.0.1'; nodeId = $registeredNode.id; maxHops = 2; timeoutMs = 100 }
    Assert-Check ($remote.StatusCode -eq 202) 'Remote loopback trace is accepted'
    $remoteResult = Wait-Trace $remote.Body.id 'real Agent ntrace completes with default GeoIP enabled' 60
    Assert-Check ($remoteResult.status -eq 'completed' -and $remoteResult.nodeId -eq $registeredNode.id -and
        $remoteResult.tool -eq 'ntrace' -and $remoteResult.exitCode -eq 0 -and $remoteResult.output -match '127\.0\.0\.1') 'Real Agent executes ntrace and reports actual output to the center'
    $terminalReport = @{ nodeId = $registeredNode.id; traceId = $remoteResult.id; status = $remoteResult.status;
        tool = $remoteResult.tool; output = $remoteResult.output; exitCode = $remoteResult.exitCode; truncated = $remoteResult.truncated }
    $acknowledged = Invoke-Api '/api/agent/traces/report' 'POST' $terminalReport $agentToken
    Assert-Check ($acknowledged.StatusCode -eq 200 -and $acknowledged.Body.accepted) 'Duplicate terminal Agent report is safely acknowledged'

    $phantomId = 'trace-phantom-' + [Guid]::NewGuid().ToString('N')
    $registration = Invoke-Api '/api/agent/register' 'POST' @{ id = $phantomId; name = 'Queued-only test node'; address = '127.0.0.1' } $agentToken
    Assert-Check ($registration.StatusCode -eq 200 -and $registration.Body.nodeId -eq $phantomId) 'Phantom node registers for deterministic queue tests'
    $oldNode = @((Invoke-Api '/api/nodes').Body | Where-Object id -eq $phantomId)[0]
    Assert-Check ($oldNode.traceVersion -eq 0) 'Agent registration without a trace version stays compatible with old Agents'
    foreach ($advancedOptions in @(@{ protocol = 'tcp'; port = 443 }, @{ psize = 64 }, @{ protocol = 'tcp'; port = 443; psize = 64 })) {
        $oldAgentRequest = @{ host = '127.0.0.1'; nodeId = $phantomId; maxHops = 2; timeoutMs = 100 }
        foreach ($key in $advancedOptions.Keys) { $oldAgentRequest[$key] = $advancedOptions[$key] }
        $oldAgentResponse = Invoke-Api '/api/traces' 'POST' $oldAgentRequest
        Assert-Check ($oldAgentResponse.StatusCode -eq 409 -and $oldAgentResponse.Body.error -match '升级|更新' -and
            [string]::IsNullOrEmpty($oldAgentResponse.Body.id)) "Old Agent must upgrade before receiving advanced trace options: $($advancedOptions | ConvertTo-Json -Compress)"
    }
    $queuedRequest = @{ host = '127.0.0.1'; nodeId = $phantomId; maxHops = 2; timeoutMs = 100 }
    $queuedOne = Invoke-Api '/api/traces' 'POST' $queuedRequest
    $queuedTwo = Invoke-Api '/api/traces' 'POST' $queuedRequest
    Assert-Check ($queuedOne.StatusCode -eq 202 -and $queuedTwo.StatusCode -eq 202) 'Two queued diagnostics are accepted for one node'
    Assert-Check ((Invoke-Api '/api/traces' 'POST' $queuedRequest).StatusCode -eq 429) 'Third diagnostic for the same node is rejected at the concurrency limit'
    $assignments = Invoke-Api "/api/agent/traces?nodeId=$phantomId" -Token $agentToken
    Assert-Check ($assignments.StatusCode -eq 200 -and @($assignments.Body.traces).Count -eq 2) 'Agent receives exactly its two queued assignments'
    Assert-Check (@($assignments.Body.traces | Where-Object { $_.protocol -eq 'icmp' -and $null -eq $_.port -and $null -eq $_.psize }).Count -eq 2) 'Basic ICMP assignments expose explicit compatible defaults'
    $wrongReport = @{ nodeId = $registeredNode.id; traceId = $queuedOne.Body.id; status = 'completed'; tool = 'ntrace'; output = 'wrong node'; exitCode = 0 }
    Assert-Check ((Invoke-Api '/api/agent/traces/report' 'POST' $wrongReport $agentToken).StatusCode -eq 400) 'A node cannot report a trace assigned to another node'
    $cancelled = Invoke-Api "/api/traces/$($queuedOne.Body.id)" 'DELETE'
    Assert-Check ($cancelled.StatusCode -eq 200 -and $cancelled.Body.status -eq 'cancelled') 'Queued trace can be cancelled before Agent execution'
    $cancelledAgain = Invoke-Api "/api/traces/$($queuedOne.Body.id)" 'DELETE'
    $cancelledRead = Invoke-Api "/api/traces/$($queuedOne.Body.id)"
    Assert-Check ($cancelledAgain.StatusCode -eq 200 -and $cancelledAgain.Body.status -eq 'cancelled' -and
        $cancelledRead.Body.status -eq 'cancelled') 'Trace cancellation is idempotent and remains readable'
    $afterCancel = Invoke-Api "/api/agent/traces?nodeId=$phantomId" -Token $agentToken
    Assert-Check (@($afterCancel.Body.traces).Count -eq 1 -and $afterCancel.Body.traces[0].id -eq $queuedTwo.Body.id) 'Cancelled trace is omitted from the authoritative Agent assignment list'
    $lateReport = @{ nodeId = $phantomId; traceId = $queuedOne.Body.id; status = 'completed'; tool = 'ntrace'; output = 'late result'; exitCode = 0; error = 'late error' }
    $lateResponse = Invoke-Api '/api/agent/traces/report' 'POST' $lateReport $agentToken
    $stillCancelled = (Invoke-Api "/api/traces/$($queuedOne.Body.id)").Body
    Assert-Check ($lateResponse.StatusCode -eq 200 -and $lateResponse.Body.accepted -and
        $stillCancelled.status -eq 'cancelled' -and $stillCancelled.output -ne 'late result') 'Late terminal reports are acknowledged without resurrecting or replacing a cancelled trace'
    Invoke-Api "/api/traces/$($queuedTwo.Body.id)" 'DELETE' | Out-Null

    $updatedRegistration = Invoke-Api '/api/agent/register' 'POST' @{ id = $phantomId; name = 'Updated queued-only test node'; address = '127.0.0.1'; traceVersion = 2 } $agentToken
    Assert-Check ($updatedRegistration.StatusCode -eq 200) 'Updated Agent can advertise version 2 after its initial registration'
    $updatedNode = @((Invoke-Api '/api/nodes').Body | Where-Object id -eq $phantomId)[0]
    Assert-Check ($updatedNode.traceVersion -eq 2) 'Node metadata reflects the latest Agent capability'
    $advancedRequest = @{ host = '127.0.0.1'; nodeId = $phantomId; maxHops = 2; timeoutMs = 100; protocol = 'tcp'; port = 443; psize = 64 }
    $advanced = Invoke-Api '/api/traces' 'POST' $advancedRequest
    Assert-Check ($advanced.StatusCode -eq 202 -and $advanced.Body.protocol -eq 'tcp' -and
        $advanced.Body.port -eq 443 -and $advanced.Body.psize -eq 64) 'TCP port 443 with packet size 64 is retained in the created task'
    $companionBasic = Invoke-Api '/api/traces' 'POST' $queuedRequest
    Assert-Check ($companionBasic.StatusCode -eq 202) 'Basic ICMP task can coexist with an advanced task on an updated Agent'
    foreach ($legacyQuery in @('', '&traceVersion=0', '&traceVersion=1')) {
        $legacyAssignments = Invoke-Api "/api/agent/traces?nodeId=$phantomId$legacyQuery" -Token $agentToken
        Assert-Check ($legacyAssignments.StatusCode -eq 200 -and @($legacyAssignments.Body.traces).Count -eq 1 -and
            $legacyAssignments.Body.traces[0].id -eq $companionBasic.Body.id -and
            $legacyAssignments.Body.traces[0].protocol -eq 'icmp') "Legacy polling receives only basic ICMP even when the latest registration advertises version 2: query='$legacyQuery'"
    }
    $advancedAssignments = Invoke-Api "/api/agent/traces?nodeId=$phantomId&traceVersion=2" -Token $agentToken
    $advancedAssignment = @($advancedAssignments.Body.traces | Where-Object id -eq $advanced.Body.id)
    Assert-Check (@($advancedAssignments.Body.traces).Count -eq 2 -and $advancedAssignment.Count -eq 1 -and $advancedAssignment[0].protocol -eq 'tcp' -and
        $advancedAssignment[0].port -eq 443 -and $advancedAssignment[0].psize -eq 64) 'Agent assignment preserves TCP, port and psize together'
    foreach ($incorrectTool in @('tracert', $null)) {
        $incorrectCompletion = @{ nodeId = $phantomId; traceId = $advanced.Body.id; status = 'completed'; tool = $incorrectTool; output = 'incorrect tool result'; exitCode = 0 }
        $incorrectCompletionResponse = Invoke-Api '/api/agent/traces/report' 'POST' $incorrectCompletion $agentToken
        $unchangedAdvanced = (Invoke-Api "/api/traces/$($advanced.Body.id)").Body
        Assert-Check ($incorrectCompletionResponse.StatusCode -eq 400 -and $unchangedAdvanced.status -eq 'queued') "Advanced task cannot be marked completed by an unsupported or unspecified tool: $incorrectTool"
    }
    Invoke-Api "/api/traces/$($advanced.Body.id)" 'DELETE' | Out-Null
    Invoke-Api "/api/traces/$($companionBasic.Body.id)" 'DELETE' | Out-Null

    foreach ($validOptions in @(
        @{ host = '127.0.0.1'; protocol = 'icmp'; psize = 28 },
        @{ host = '127.0.0.1'; protocol = 'tcp'; port = 1; psize = 40 },
        @{ host = '::1'; protocol = 'icmp'; psize = 48 },
        @{ host = '::1'; protocol = 'tcp'; port = 65535; psize = 60 },
        @{ host = 'example.com'; protocol = 'icmp'; psize = 28 },
        @{ host = 'example.com'; protocol = 'tcp'; port = 443; psize = 40 },
        @{ host = '127.0.0.1'; protocol = 'tcp'; port = 443; psize = 65535 },
        @{ host = '127.0.0.1'; protocol = 'icmp'; port = $null; psize = $null }
    )) {
        $validRequest = @{ nodeId = $phantomId; maxHops = 2; timeoutMs = 100 }
        foreach ($key in $validOptions.Keys) { $validRequest[$key] = $validOptions[$key] }
        $acceptedOptions = Invoke-Api '/api/traces' 'POST' $validRequest
        Assert-Check ($acceptedOptions.StatusCode -eq 202) "Valid trace option bounds are accepted without privileged execution: $($validOptions | ConvertTo-Json -Compress)"
        Invoke-Api "/api/traces/$($acceptedOptions.Body.id)" 'DELETE' | Out-Null
    }

    $beforeDowngrade = Invoke-Api '/api/traces' 'POST' $advancedRequest
    Assert-Check ($beforeDowngrade.StatusCode -eq 202) 'Advanced task is queued before an Agent capability downgrade'
    $downgradedRegistration = Invoke-Api '/api/agent/register' 'POST' @{ id = $phantomId; name = 'Old Agent replacing new Agent'; address = '127.0.0.1' } $agentToken
    $downgradedTask = (Invoke-Api "/api/traces/$($beforeDowngrade.Body.id)").Body
    $afterDowngrade = Invoke-Api "/api/agent/traces?nodeId=$phantomId" -Token $agentToken
    Assert-Check ($downgradedRegistration.StatusCode -eq 200 -and $downgradedTask.status -eq 'failed' -and
        $downgradedTask.error -match '升级|更新' -and @($afterDowngrade.Body.traces).Count -eq 0) 'Old Agent replacement fails and removes advanced queued tasks before they can run with ignored options'

    Stop-Monitor $agent
    Stop-Monitor $center
    # The fallback copy intentionally omits the bundled tool. The normal migrated copy
    # above must keep using NextTrace even when the execution host has no PATH tools.
    $fallbackBuildDirectory = Join-Path $temporaryDirectory 'fallback-build'
    New-Item -ItemType Directory -Path $fallbackBuildDirectory | Out-Null
    Get-ChildItem -LiteralPath $buildDirectory -Force | Where-Object { $_.Name -ne 'tools' } |
        Copy-Item -Destination $fallbackBuildDirectory -Recurse -Force
    $fallbackAssembly = Join-Path $fallbackBuildDirectory 'SmartPing.dll'
    $fallbackArguments = @(
        "--urls=$baseUrl", '--SmartPing:Mode=server',
        "--SmartPing:DataDirectory=$(Join-Path $temporaryDirectory 'fallback')",
        '--SmartPing:NodeName=Fallback integration center', '--SmartPing:NodeAddress=127.0.0.1')
    $fallback = Start-Monitor 'unbundled PATH-empty fallback center' $fallbackArguments $true $fallbackAssembly
    Wait-Until { (Invoke-Api '/api/health' -Token '').StatusCode -eq 200 } 'fallback center health' 20
    $fallbackTool = Invoke-Api '/api/traces/tools'
    Assert-Check ($fallbackTool.StatusCode -eq 200 -and $fallbackTool.Body.available -and $fallbackTool.Body.tool -eq 'tracert') 'Only an absent bundle and empty PATH select the Windows system tracert fallback'
    Assert-Check (-not $fallbackTool.Body.supportsTcp -and -not $fallbackTool.Body.supportsPsize) 'Windows tracert correctly advertises no TCP or packet-size support'
    foreach ($unsupportedOptions in @(@{ protocol = 'tcp'; port = 443 }, @{ psize = 64 }, @{ protocol = 'tcp'; port = 443; psize = 64 })) {
        $unsupportedRequest = @{ host = '127.0.0.1'; nodeId = 'local'; maxHops = 2; timeoutMs = 100 }
        foreach ($key in $unsupportedOptions.Keys) { $unsupportedRequest[$key] = $unsupportedOptions[$key] }
        $unsupported = Invoke-Api '/api/traces' 'POST' $unsupportedRequest
        Assert-Check ($unsupported.StatusCode -eq 409 -and $unsupported.Body.error -match 'ntrace' -and
            [string]::IsNullOrEmpty($unsupported.Body.id)) "tracert fallback rejects unsupported options without silently dropping them: $($unsupportedOptions | ConvertTo-Json -Compress)"
    }
    $fallbackTrace = Invoke-Api '/api/traces' 'POST' $localRequest
    Assert-Check ($fallbackTrace.StatusCode -eq 202) 'Fallback loopback trace is accepted'
    $fallbackResult = Wait-Trace $fallbackTrace.Body.id 'real tracert fallback completes' 20
    Assert-Check ($fallbackResult.status -eq 'completed' -and $fallbackResult.tool -eq 'tracert' -and
        $fallbackResult.exitCode -eq 0 -and $fallbackResult.output -match '127\.0\.0\.1') 'Real Windows tracert completes with actual loopback output'
    $completed = $true
    Write-Host "Traceroute integration passed: $checkCount checks."
}
finally {
    foreach ($entry in $processes) {
        try { Stop-Monitor $entry } catch { Write-Warning $_ }
    }
    if (-not $completed) {
        foreach ($entry in $processes) {
            if ($entry.Process.HasExited) {
                Write-Host "Process log: $($entry.Label)"
                ($entry.Output.GetAwaiter().GetResult() + $entry.Error.GetAwaiter().GetResult()) -split '\r?\n' | Select-Object -Last 20 | Write-Host
            }
        }
    }
    foreach ($entry in $processes) { $entry.Process.Dispose() }
    $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
    $expectedParent = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ((Split-Path -Parent $resolvedTemporaryDirectory) -ne $expectedParent -or
        (Split-Path -Leaf $resolvedTemporaryDirectory) -ne $temporaryName -or
        -not $temporaryName.StartsWith('SmartPingTraceroute-')) {
        throw "Refusing to remove unexpected test path: $resolvedTemporaryDirectory"
    }
    if (Test-Path -LiteralPath $resolvedTemporaryDirectory) {
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }
}
