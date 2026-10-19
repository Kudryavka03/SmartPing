#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src/SmartPing/SmartPing.csproj'
$contentDirectory = Split-Path -Parent $projectPath
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryName = 'SmartPingHistoryWindows-' + [Guid]::NewGuid().ToString('N')
$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot $temporaryName))
$buildDirectory = Join-Path $temporaryDirectory 'build'
$adminToken = [Guid]::NewGuid().ToString('N')
$agentToken = [Guid]::NewGuid().ToString('N')
$center = $null
$completed = $false
$checkCount = 0

function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $script:checkCount++
    Write-Host "PASS $Description"
}

function Invoke-Api([string]$Path, [string]$Method = 'GET', $Body = $null, [string]$Token = $adminToken) {
    $parameters = @{
        Uri = $baseUrl + $Path; Method = $Method; SkipHttpErrorCheck = $true
        StatusCodeVariable = 'statusCode'; TimeoutSec = 15
        Headers = @{ Authorization = "Bearer $Token" }
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress))
    }
    $payload = Invoke-RestMethod @parameters
    return [PSCustomObject]@{ StatusCode = [int]$statusCode; Body = $payload }
}

function Get-History([string]$TargetId, [string]$Query = '') {
    $response = Invoke-Api "/api/targets/$TargetId/history$Query"
    if ($response.StatusCode -ne 200) { throw "History returned $($response.StatusCode): $Query" }
    return $response.Body
}

function New-Fixture([string]$Name, [string]$Protocol = 'icmp', [string[]]$Nodes = @('window-a')) {
    $request = @{
        name = $Name; host = '192.0.2.1'; protocol = $Protocol; port = 443
        intervalSeconds = 3600; timeoutMs = 2000; retransmitThresholdMs = 100
        nodeIds = $Nodes; group = 'History window regression'; enabled = $false
    }
    $response = Invoke-Api '/api/targets' 'POST' $request
    Assert-Check ($response.StatusCode -eq 201 -and -not $response.Body.enabled) "Paused fixture $Name is created without live probes"
    return $response.Body
}

function New-Sample([string]$TargetId, [string]$Id, [DateTimeOffset]$Time, $Latency = 10, [bool]$Success = $true, [string]$NodeId = 'window-a') {
    return @{
        id = $Id; targetId = $TargetId; nodeId = $NodeId; timestamp = $Time.ToString('o')
        latencyMs = $Latency; success = $Success; suspectedRetransmit = $false
        error = $(if ($Success) { $null } else { 'Timeout' })
    }
}

function Send-Samples([array]$Samples, [string]$NodeId = 'window-a') {
    $response = Invoke-Api '/api/agent/report' 'POST' @{ nodeId = $NodeId; samples = $Samples } $agentToken
    Assert-Check ($response.StatusCode -eq 200 -and $response.Body.accepted -eq $Samples.Count) "$($Samples.Count) synthetic samples are accepted for $NodeId"
}

function Sum-Sent($Series) { return [long](($Series | Measure-Object -Property sent -Sum).Sum) }
function Near($Actual, [double]$Expected) { return $null -ne $Actual -and [Math]::Abs([double]$Actual - $Expected) -lt 0.000001 }

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    $buildOutput = & $dotnetPath build $projectPath -c Release --nologo -o $buildDirectory -p:UseAppHost=false 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($buildOutput -join [Environment]::NewLine) }
    Assert-Check $true 'Isolated Release build succeeds'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnetPath
    $startInfo.WorkingDirectory = $contentDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @((Join-Path $buildDirectory 'SmartPing.dll'), "--urls=$baseUrl", '--SmartPing:Mode=server',
        "--SmartPing:DataDirectory=$(Join-Path $temporaryDirectory 'center')", '--SmartPing:NodeName=Window test center')) {
        $startInfo.ArgumentList.Add($argument)
    }
    foreach ($key in @($startInfo.Environment.Keys)) {
        if ($key -like 'SmartPing__*' -or $key -like 'ASPNETCORE_*' -or $key -eq 'DOTNET_ENVIRONMENT') {
            $startInfo.Environment.Remove($key) | Out-Null
        }
    }
    $startInfo.Environment['DOTNET_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['SmartPing__AdminToken'] = $adminToken
    $startInfo.Environment['SmartPing__AgentToken'] = $agentToken
    $center = [Diagnostics.Process]::new()
    $center.StartInfo = $startInfo
    if (-not $center.Start()) { throw 'Could not start isolated center.' }
    $centerOutput = $center.StandardOutput.ReadToEndAsync()
    $centerError = $center.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        try { if ((Invoke-Api '/api/health').StatusCode -eq 200) { break } } catch { }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    Assert-Check ((Invoke-Api '/api/health').StatusCode -eq 200) 'Isolated center starts'
    foreach ($node in @('window-a', 'window-b', 'window-empty')) {
        $response = Invoke-Api '/api/agent/register' 'POST' @{ id = $node; name = $node; address = '192.0.2.20' } $agentToken
        Assert-Check ($response.StatusCode -eq 200) "Synthetic Agent $node registers"
    }
    $windowTarget = New-Fixture 'window-fixture' -Nodes @('window-a', 'window-b')
    $denseTarget = New-Fixture 'selected-dense'
    $compactTarget = New-Fixture 'compact-dense'
    $tcpTarget = New-Fixture 'tcp-fixture' 'tcp'
    $now = [DateTimeOffset]::UtcNow
    $offsets = @(10, 120, 240, 420, 1200, 2700, 4200)
    $windowSamples = @(for ($index = 0; $index -lt $offsets.Count; $index++) {
        New-Sample $windowTarget.id "window-$index" $now.AddSeconds(-$offsets[$index]) ($index + 1)
    })
    Send-Samples $windowSamples
    Send-Samples @((New-Sample $windowTarget.id 'window-b-current' $now.AddSeconds(-10) 100 -NodeId 'window-b')) 'window-b'
    foreach ($target in @($denseTarget, $compactTarget)) {
        $denseSamples = @(for ($index = 0; $index -lt 1700; $index++) {
            New-Sample $target.id "$($target.id)-$index" $now.AddSeconds(-10 - 2 * $index) 10
        })
        Send-Samples $denseSamples
    }
    $tcpSamples = @(
        (New-Sample $tcpTarget.id 'tcp-01' $now.AddSeconds(-16) 10),
        (New-Sample $tcpTarget.id 'tcp-02' $now.AddSeconds(-15) 12),
        (New-Sample $tcpTarget.id 'tcp-03' $now.AddSeconds(-14) 500),
        (New-Sample $tcpTarget.id 'tcp-04' $now.AddSeconds(-13) 20),
        (New-Sample $tcpTarget.id 'tcp-05' $now.AddSeconds(-12) 23),
        (New-Sample $tcpTarget.id 'tcp-06' $now.AddSeconds(-11) $null $false),
        (New-Sample $tcpTarget.id 'tcp-07' $now.AddSeconds(-10) 30)
    )
    Send-Samples $tcpSamples

    $minutesList = @(1, 3, 5, 10, 30, 60)
    for ($index = 0; $index -lt $minutesList.Count; $index++) {
        $minutes = $minutesList[$index]
        $query = "?minutes=$minutes&bucketSeconds=5&nodeId=window-a"
        $history = Get-History $windowTarget.id $query
        $dashboardResponse = Invoke-Api "/api/dashboard$query&seriesTargetId=$($windowTarget.id)"
        $dashboard = $dashboardResponse.Body
        $card = @($dashboard.targets | Where-Object id -eq $windowTarget.id)[0]
        Assert-Check ($history.stats.sent -eq $index + 1 -and (Sum-Sent $history.series) -eq $index + 1) "$minutes-minute history excludes older samples without widening the window"
        Assert-Check ($dashboardResponse.StatusCode -eq 200 -and $card.stats.sent -eq $history.stats.sent -and
            $card.stats.avgLatencyMs -eq $history.stats.avgLatencyMs -and $card.stats.jitterMs -eq $history.stats.jitterMs) "$minutes-minute dashboard statistics match target history"
        Assert-Check ($dashboard.windowMinutes -eq $minutes -and $dashboard.requestedBucketSeconds -eq 5 -and
            $dashboard.bucketSeconds -eq $history.bucketSeconds -and $card.bucketSeconds -eq $history.bucketSeconds -and
            $dashboard.seriesTargetId -eq $windowTarget.id) "$minutes-minute dashboard discloses its requested and actual aggregation intervals"
    }
    $custom = Get-History $windowTarget.id '?minutes=7&bucketSeconds=1&nodeId=window-a'
    Assert-Check ($custom.stats.sent -eq 3 -and $custom.bucketSeconds -eq 1) 'A custom seven-minute window uses one-second aggregation'
    Assert-Check ((Get-History $windowTarget.id '?minutes=1').stats.sent -eq 2 -and
        (Get-History $windowTarget.id '?minutes=1&nodeId=window-b').stats.avgLatencyMs -eq 100) 'Custom windows respect combined and individual node filters'
    $empty = Get-History $windowTarget.id '?minutes=1&bucketSeconds=1&nodeId=window-empty'
    Assert-Check ($empty.stats.sent -eq 0 -and @($empty.series).Count -eq 0 -and $empty.stats.lossPercent -eq 0) 'An empty source has no fabricated loss or empty chart buckets'
    $selectedQuery = "?minutes=60&bucketSeconds=1&nodeId=window-a&seriesTargetId=$($denseTarget.id)"
    $selectedDashboard = (Invoke-Api "/api/dashboard$selectedQuery").Body
    $selectedCard = @($selectedDashboard.targets | Where-Object id -eq $denseTarget.id)[0]
    $compactCard = @($selectedDashboard.targets | Where-Object id -eq $compactTarget.id)[0]
    Assert-Check ($selectedCard.bucketSeconds -eq 1 -and @($selectedCard.series).Count -eq 1700 -and
        (Sum-Sent $selectedCard.series) -eq 1700) 'Selected dashboard link retains the requested high-resolution series'
    Assert-Check (@($compactCard.series).Count -le 240 -and $compactCard.bucketSeconds -ge 15 -and
        (Sum-Sent $compactCard.series) -eq 1700 -and $compactCard.stats.sent -eq 1700) 'Other dashboard links retain compact series with explicit intervals and unchanged totals'
    $selectedHistory = Get-History $denseTarget.id '?minutes=60&bucketSeconds=1&nodeId=window-a'
    Assert-Check (@($selectedHistory.series).Count -eq @($selectedCard.series).Count -and $selectedHistory.bucketSeconds -eq $selectedCard.bucketSeconds) 'Selected chart resolution matches the target history endpoint'
    foreach ($seconds in @(1, 5, 15, 30, 3600)) {
        $history = Get-History $tcpTarget.id "?minutes=1&bucketSeconds=$seconds"
        Assert-Check ($history.bucketSeconds -eq $seconds -and $history.stats.sent -eq 7 -and
            $history.stats.received -eq 6 -and (Near $history.stats.avgLatencyMs 19) -and
            (Near $history.stats.jitterMs 2.5) -and $history.stats.avgLatencySampleCount -eq 5 -and
            (Sum-Sent $history.series) -eq 7) "$seconds-second TCP aggregation preserves retransmit exclusions, pair breaks and delivery totals"
    }
    $defaultHistory = Get-History $windowTarget.id
    $defaultDashboard = (Invoke-Api '/api/dashboard').Body
    Assert-Check ($defaultHistory.bucketSeconds -eq 15 -and $defaultDashboard.windowMinutes -eq 60 -and
        $defaultDashboard.requestedBucketSeconds -eq 15) 'Legacy calls default to one hour and fifteen-second aggregation'
    $fractionalHours = Get-History $windowTarget.id '?hours=0.016666666666666666&nodeId=window-a'
    Assert-Check ($fractionalHours.stats.sent -eq 1) 'Legacy fractional hours support one-minute windows'
    $lowerHours = (Invoke-Api '/api/dashboard?hours=0').Body
    $upperHours = (Invoke-Api '/api/dashboard?hours=999').Body
    Assert-Check ((Near $lowerHours.windowMinutes 1) -and $upperHours.windowMinutes -eq 10080) 'Legacy hours remain clamped to one minute through seven days'
    $longHistory = Get-History $denseTarget.id '?hours=168&bucketSeconds=1'
    Assert-Check ($longHistory.bucketSeconds -ge 168 -and @($longHistory.series).Count -le 3600 -and
        (Sum-Sent $longHistory.series) -eq 1700) 'Long ranges disclose coarser bounded aggregation without losing samples'

    foreach ($query in @('minutes=0', 'minutes=-1', 'minutes=61', 'minutes=1.5', 'minutes=text',
        'bucketSeconds=0', 'bucketSeconds=-1', 'bucketSeconds=3601', 'bucketSeconds=1.5', 'bucketSeconds=text',
        'bucketSeconds=2147483648', 'minutes=2147483648', 'minutes=1&hours=1', 'hours=NaN', 'hours=Infinity')) {
        foreach ($endpoint in @('/api/dashboard', "/api/targets/$($windowTarget.id)/history")) {
            Assert-Check ((Invoke-Api "$endpoint`?$query").StatusCode -eq 400) "$endpoint rejects invalid query $query"
        }
    }
    Assert-Check (@((Invoke-Api '/api/targets').Body | Where-Object enabled).Count -eq 0) 'All test projects remain paused; no real probe or traceroute driver is used'
    $completed = $true
    Write-Host "History window integration passed: $checkCount checks."
}
finally {
    if ($null -ne $center) {
        if (-not $center.HasExited) {
            $center.Kill($true)
            if (-not $center.WaitForExit(5000)) { throw 'Failed to stop isolated history test center.' }
        }
        if (-not $completed) {
            Write-Host 'Isolated center log:'
            ($centerOutput.GetAwaiter().GetResult() + $centerError.GetAwaiter().GetResult()) -split '\r?\n' | Select-Object -Last 15 | Write-Host
        }
        $center.Dispose()
    }
    $resolved = [IO.Path]::GetFullPath($temporaryDirectory)
    $expectedParent = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ((Split-Path -Parent $resolved) -ne $expectedParent -or (Split-Path -Leaf $resolved) -ne $temporaryName -or
        -not $temporaryName.StartsWith('SmartPingHistoryWindows-')) { throw "Refusing to remove unexpected test path: $resolved" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
