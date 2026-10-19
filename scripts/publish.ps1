[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src/SmartPing/SmartPing.csproj'
$bundleRelativePath = 'tools/nexttrace'
$bundleSource = Join-Path $projectRoot "src/SmartPing/$bundleRelativePath"

function Assert-NextTraceBundle([string]$BundlePath) {
    $manifestPath = Join-Path $BundlePath 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "NextTrace manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.runtime -ne 'win-x64') {
        throw 'Unsupported NextTrace bundle manifest.'
    }
    $requiredPaths = @('win-x64/ntrace.exe', 'win-x64/WinDivert.dll', 'win-x64/WinDivert64.sys')
    $manifestPaths = @($manifest.files | ForEach-Object { $_.path })
    if ($manifestPaths.Count -ne $requiredPaths.Count -or
        @($requiredPaths | Where-Object { $_ -cnotin $manifestPaths }).Count -ne 0) {
        throw 'NextTrace manifest must contain exactly the expected executable, DLL and driver.'
    }
    foreach ($entry in $manifest.files) {
        $filePath = Join-Path $BundlePath $entry.path
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Bundled runtime file is missing: $($entry.path)"
        }
        $file = Get-Item -LiteralPath $filePath
        $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($file.Length -ne $entry.size -or $entry.sha256 -notmatch '^[a-f0-9]{64}$' -or
            $hash -ine $entry.sha256) {
            throw "Bundled runtime hash or size mismatch: $($entry.path)"
        }
    }
    foreach ($notice in @('NOTICE.md', 'LICENSE.NextTrace.txt', 'LICENSE.WinDivert.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $BundlePath $notice) -PathType Leaf)) {
            throw "Bundled runtime notice is missing: $notice"
        }
    }
}

# Validate the pinned repository files before publishing. Nothing is downloaded
# or initialized during publishing or application startup.
Assert-NextTraceBundle $bundleSource
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install the .NET 8 SDK.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "artifacts/publish/$Runtime"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& dotnet publish $projectPath -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE." }
if ($Runtime -eq 'win-x64') {
    Assert-NextTraceBundle (Join-Path $OutputDirectory $bundleRelativePath)
} else {
    Write-Warning 'NextTrace/WinDivert is bundled only for Windows x64. ARM64 needs a compatible external ntrace, or uses tracert for basic ICMP tracing.'
}
Write-Host "Published to $OutputDirectory"
Write-Host 'Deploy the entire directory, including wwwroot, appsettings.json and tools/nexttrace.'
