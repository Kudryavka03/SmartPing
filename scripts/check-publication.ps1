#requires -Version 7.0
<#
.SYNOPSIS
Check the exact Git index snapshot before publishing, or an explicit file list.
.DESCRIPTION
Reject runtime data, credentials, personal paths, and non-documentation IPs.
The only binary exception is the pinned NextTrace/WinDivert bundle, with its
manifest and licenses present in the same candidate snapshot and hashes intact.
Findings contain locations and categories only; matched values are never printed.
#>
[CmdletBinding()]
param(
    [string[]]$Files,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$entries = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::OrdinalIgnoreCase)
$findings = [Collections.Generic.List[object]]::new()
$encoding = [Text.UTF8Encoding]::new($false, $true)
$vendorRoot = 'src/SmartPing/tools/nexttrace/'
$approvedBinaries = @{
    'win-x64/ntrace.exe' = '2b64b03167304be22f6f7853516512dd7c848a341f694071e0a7bbca7054bfb3'
    'win-x64/WinDivert.dll' = 'c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2'
    'win-x64/WinDivert64.sys' = '8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2'
}
$approvedLicenses = @{
    'LICENSE.NextTrace.txt' = '3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986'
    'LICENSE.WinDivert.txt' = '14a0cb5214d536e4fdae6aa3f5696f981eeda106cd026e9794bba489ee79d628'
}

function Add-Finding([string]$Path, [int]$Line, [string]$Category) {
    $findings.Add([PSCustomObject]@{ File = $Path; Line = $Line; Category = $Category })
}

function Invoke-GitBytes([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.WorkingDirectory = $repository
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $buffer = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) { throw 'Could not start Git.' }
        $errorRead = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($buffer)
        $process.WaitForExit()
        $null = $errorRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Cannot read Git index. Stage the intended publication files first, or use -Files.' }
        return [PSCustomObject]@{ Bytes = $buffer.ToArray() }
    }
    finally { $buffer.Dispose(); $process.Dispose() }
}

if ($Files.Count -gt 0) {
    foreach ($candidate in $Files) {
        $absolute = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($candidate)) { $candidate } else { Join-Path $repository $candidate }))
        if (-not $absolute.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Candidate files must stay within the repository.'
        }
        $relative = [IO.Path]::GetRelativePath($repository, $absolute).Replace('\', '/')
        $item = Get-Item -LiteralPath $absolute -Force
        if ($item.PSIsContainer -or $item.LinkType) { Add-Finding $relative 0 'directory-or-link'; continue }
        $entries[$relative] = [IO.File]::ReadAllBytes($absolute)
    }
    $snapshot = 'explicit candidate files'
}
else {
    $indexBytes = (Invoke-GitBytes @('ls-files', '--stage', '-z')).Bytes
    foreach ($record in $encoding.GetString($indexBytes).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($record -notmatch '^([0-9]{6}) ([0-9a-f]{40,64}) ([0-3])\t(.+)$') { throw 'Unexpected Git index record.' }
        $mode, $objectId, $stage, $path = $Matches[1], $Matches[2], $Matches[3], $Matches[4]
        if ($stage -ne '0' -or $mode -notin @('100644', '100755')) { Add-Finding $path 0 'link-submodule-or-unmerged-entry'; continue }
        $entries[$path] = (Invoke-GitBytes @('cat-file', 'blob', $objectId)).Bytes
    }
    $snapshot = 'Git index (staged contents)'
}
if ($entries.Count -eq 0) { throw 'No candidate files to check.' }

function Test-SafeIp([Net.IPAddress]$Address, [bool]$AllowBroadcast) {
    if ($Address.IsIPv4MappedToIPv6) { return Test-SafeIp $Address.MapToIPv4() $AllowBroadcast }
    $bytes = $Address.GetAddressBytes()
    if ($bytes.Length -eq 4) {
        if ($bytes[0] -eq 127 -or ($bytes | Where-Object { $_ -ne 0 }).Count -eq 0) { return $true }
        if (($bytes[0] -eq 192 -and $bytes[1] -eq 0 -and $bytes[2] -eq 2) -or
            ($bytes[0] -eq 198 -and $bytes[1] -eq 51 -and $bytes[2] -eq 100) -or
            ($bytes[0] -eq 203 -and $bytes[1] -eq 0 -and $bytes[2] -eq 113)) { return $true }
        # The broadcast boundary is a validator fixture, never a monitor target.
        return $AllowBroadcast -and ($bytes | Where-Object { $_ -ne 255 }).Count -eq 0
    }
    if ([Net.IPAddress]::IsLoopback($Address) -or $Address.Equals([Net.IPAddress]::IPv6Any)) { return $true }
    return $bytes[0] -eq 0x20 -and $bytes[1] -eq 1 -and $bytes[2] -eq 0x0d -and $bytes[3] -eq 0xb8
}

function Test-Placeholder([string]$Value) {
    return [string]::IsNullOrEmpty($Value) -or $Value -match '^(?:<[^>]+>|\$\{?\w+\}?|(?i:example|placeholder|change[-_ ]?me|your[-_ ]|test[-_]|dummy|not[-_ ]?a[-_ ]?real))' -or
        $Value -match '替换|令牌'
}

$personalWords = @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ -and $_.Length -ge 4 -and $_ -notmatch '^(?i:user|admin|runner|root|localhost)$' }
$workspaceForms = @($repository, $repository.Replace('\', '\\'), $repository.Replace('\', '/'))
$privatePathPattern = '(?i)(?:[A-Z]:[\\/]+Users[\\/]+[^\\/\s"'']+|/(?:home|Users)/[^/\s"'']+|\b(?:DESKTOP|LAPTOP)-[A-Z0-9]{4,}\b)'
$forbiddenPathPattern = '(?i)(?:^|/)(?:bin|obj|artifacts|data|logs?|screenshots?|TestResults|test-results|test-output|coverage|cache|\.cache|\.vs|\.vscode|\.idea|node_modules|\.git|\.ssh|\.aws|\.azure|\.codex|Users|Desktop|Downloads|Documents)(?:/|$)'
$forbiddenNamePattern = '(?i)(?:^|/)(?:\.env[^/]*|appsettings\.[^/]+\.json|launchSettings\.json|secrets\.json|[^/]*(?:credentials|secrets)\.json|[^/]+\.(?:db(?:-(?:shm|wal))?|sqlite3?(?:-[^/]*)?|log|har|pcapng?|dmp|dump|pdb|tmp|bak|pem|key|pfx|p12|crt|cer|keystore|jks|srl|user|suo)|[^/]+\.local\.(?:json|config)|[^/]+\.config\.local|[^/]+\.(?:runtimeconfig|deps|Development|Production|Staging)\.json|[^/]*_history|[^/]+\.history)$'
$credentialPattern = '(?i)(?<![A-Za-z0-9])(?:SmartPing(?:::|__|:))?(?:AdminToken|AgentToken|Api[_-]?Key|Api[_-]?Token|Access[_-]?Token|Refresh[_-]?Token|Client[_-]?Secret|Password|Secret|Token)["'']?\s*(?:=|:)\s*["''](?<value>[^"''\r\n]*)["'']'
$secretPatterns = @(
    '\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,})\b',
    '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b',
    '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----',
    '(?i)\bBearer\s+[A-Za-z0-9._~+/-]{16,}=*',
    '(?i)\b(?:https?|postgres(?:ql)?|mysql|redis|amqps?)://[^/\s:@]+:[^/\s@]+@'
)
$ipv4Pattern = '(?<![\d])(?<![\d]\.)(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?!\d|\.\d)'
$ipv6Pattern = '(?<![0-9A-Za-z:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]*(?![0-9A-Za-z:.%])'
$validationFixturePaths = @('tests/SmartPing.Tests/TraceRunnerTests.cs', 'scripts/test-traceroute.ps1')

# Binary exceptions are deliberately restricted to these exact paths.
$verifiedBinaries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$binaryCandidates = @($entries.Keys | Where-Object { $_ -match '(?i)\.(exe|dll|sys)$' })
if ($binaryCandidates.Count -gt 0) {
    $requiredMetadata = @('manifest.json', 'NOTICE.md', 'LICENSE.NextTrace.txt', 'LICENSE.WinDivert.txt')
    $metadataPresent = $true
    foreach ($metadata in $requiredMetadata) {
        if (-not $entries.ContainsKey($vendorRoot + $metadata)) { Add-Finding ($vendorRoot + $metadata) 0 'missing-vendor-metadata'; $metadataPresent = $false }
    }
    if ($metadataPresent) {
        try {
            $manifest = $encoding.GetString($entries[$vendorRoot + 'manifest.json']) | ConvertFrom-Json
            if ($manifest.schemaVersion -ne 1 -or $manifest.runtime -ne 'win-x64') { throw 'Unsupported manifest.' }
            foreach ($license in $approvedLicenses.Keys) {
                if ($approvedLicenses[$license] -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entries[$vendorRoot + $license]))) {
                    Add-Finding ($vendorRoot + $license) 0 'vendor-license-mismatch'; $metadataPresent = $false
                }
            }
            foreach ($binary in $approvedBinaries.Keys) {
                $path = $vendorRoot + $binary
                if (-not $entries.ContainsKey($path)) { continue }
                $record = @($manifest.files | Where-Object { $_.path -ceq $binary })
                if (-not $metadataPresent -or $record.Count -ne 1 -or $record[0].sha256 -ne $approvedBinaries[$binary] -or
                    $record[0].size -ne $entries[$path].LongLength -or
                    $record[0].sha256 -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entries[$path]))) {
                    Add-Finding $path 0 'vendor-hash-or-manifest-mismatch'; continue
                }
                $null = $verifiedBinaries.Add($path)
            }
        }
        catch { Add-Finding ($vendorRoot + 'manifest.json') 0 'invalid-vendor-manifest' }
    }
}

foreach ($entry in $entries.GetEnumerator()) {
    $path, $bytes = $entry.Key, $entry.Value
    if ($path -match $forbiddenPathPattern -or $path -match $forbiddenNamePattern) { Add-Finding $path 0 'private-runtime-or-local-file' }
    if ($path -match $privatePathPattern -or @($personalWords | Where-Object { $path -match ('(?i)(?<![A-Za-z0-9])' + [Regex]::Escape($_) + '(?![A-Za-z0-9])') }).Count -gt 0) {
        Add-Finding $path 0 'personal-filename'
    }
    if ($verifiedBinaries.Contains($path)) { continue }
    if ($path -match '(?i)\.(exe|dll|sys|zip|7z|gz|tar|png|jpe?g|gif|webp|bmp|ico|pdf|docx?|xlsx?|pptx?)$' -or $bytes -contains 0) {
        Add-Finding $path 0 'unapproved-binary-or-capture'; continue
    }
    try { $content = $encoding.GetString($bytes) }
    catch { Add-Finding $path 0 'non-utf8-content'; continue }
    $lineNumber = -1
    foreach ($line in (@($path) + [Regex]::Split($content, '\r?\n'))) {
        $lineNumber++
        if ($line -match $privatePathPattern -or @($workspaceForms | Where-Object { $line.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0 -or
            @($personalWords | Where-Object { $line -match ('(?i)(?<![A-Za-z0-9])' + [Regex]::Escape($_) + '(?![A-Za-z0-9])') }).Count -gt 0) {
            Add-Finding $path $lineNumber 'personal-workspace-user-or-host'
        }
        foreach ($pattern in $secretPatterns) {
            if ($line -match $pattern) { Add-Finding $path $lineNumber 'credential-or-private-key'; break }
        }
        foreach ($match in [Regex]::Matches($line, $credentialPattern)) {
            if (-not (Test-Placeholder $match.Groups['value'].Value)) { Add-Finding $path $lineNumber 'literal-credential-assignment' }
        }
        foreach ($match in [Regex]::Matches($line, $ipv4Pattern)) {
            $text = $match.Value
            # Reject ambiguous decimal forms as application input, but retain the
            # deliberately malformed validator corpus without treating it as data.
            $octets = $text.Split('.')
            if (@($octets | Where-Object { [int]$_ -gt 255 -or ($_.Length -gt 1 -and $_.StartsWith('0')) }).Count -gt 0) {
                if ($path -notin $validationFixturePaths) { Add-Finding $path $lineNumber 'ambiguous-ip-literal' }
                continue
            }
            $address = $null
            if ([Net.IPAddress]::TryParse($text, [ref]$address) -and -not (Test-SafeIp $address ($path -in $validationFixturePaths))) {
                Add-Finding $path $lineNumber 'non-documentation-ip-address'
            }
        }
        foreach ($match in [Regex]::Matches($line, $ipv6Pattern)) {
            $text = $match.Value.TrimEnd('.')
            # Scope syntax is intentionally rejected by the traceroute validator.
            if ($path -in $validationFixturePaths -and $text -ceq ('fe80' + '::1' + '%' + '12')) { continue }
            if ($path -in $validationFixturePaths -and $text -ceq ('::ffff:' + (@('192', '168', '001', '1') -join '.'))) { continue }
            $address = $null
            if ([Net.IPAddress]::TryParse($text, [ref]$address) -and -not (Test-SafeIp $address $false)) {
                Add-Finding $path $lineNumber 'non-documentation-ip-address'
            }
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | Sort-Object File, Line, Category -Unique | Format-Table -AutoSize | Out-Host
    throw "Publication check failed: $($findings.Count) finding(s). Matched values were withheld."
}
Write-Host "Publication check passed: $($entries.Count) files from $snapshot; $($verifiedBinaries.Count) verified vendor binaries."
