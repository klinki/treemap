#Requires -Version 5.1
<#
.SYNOPSIS
    Runs a process under hard Windows job-object caps.

.DESCRIPTION
    Caps (defaults): 512 MiB per process, 1 GiB per job, 120 s wall clock.
    The launched process is assigned to the job while still suspended, so even its
    earliest descendants inherit the caps: for a benchmark run this includes the
    build process and every BenchmarkDotNet child it spawns.

    The run is killed when peak process or job memory reaches 90% of a cap, when the
    kernel reports a job memory limit, or at the deadline -- whichever comes first.
    The whole job is terminated, not just the root process.

    Fails closed. If the application cannot be resolved, or the job, completion port,
    or log file cannot be created, this throws. There is no unbounded fallback path.

    The DOTNET_GCHeapHardLimit set here is a managed heap limit. It supplements the
    kernel caps for .NET processes; it is not a machine-wide cap on its own.

.OUTPUTS
    A [BoundedProcess+Result] object, and a sibling "<log>.json" file recording exit
    code, stop reason, elapsed seconds, and peak process/job memory.
#>
[CmdletBinding()]
param(
    # Bare command name resolved through PATH, or an exact path to the executable.
    [Parameter()][string]$Application = 'dotnet',
    [Parameter(Mandatory)][AllowEmptyString()][string]$Arguments = '',
    # tools/.. is the repository root.
    [string]$WorkingDirectory = (Join-Path $PSScriptRoot '..'),
    # artifacts/ is git-ignored; the parent directory is created if missing.
    [string]$LogPath = (Join-Path $PSScriptRoot '../artifacts/verification/bounded-run.log'),
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 120,
    [ValidateRange(128, 2048)][int]$ProcessMemoryMiB = 512,
    [ValidateRange(128, 2048)][int]$JobMemoryMiB = 1024
)

$ErrorActionPreference = 'Stop'

# The job-object P/Invokes are kernel32 only. Fail closed rather than degrade.
if ($env:OS -ne 'Windows_NT') {
    throw 'Invoke-Bounded.ps1 requires Windows: the caps are enforced through kernel32 job objects.'
}

function Resolve-Application {
    param([Parameter(Mandatory)][string]$Name)

    if ($Name -match '[/\\]' -or (Test-Path -LiteralPath $Name -PathType Leaf)) {
        if (-not (Test-Path -LiteralPath $Name -PathType Leaf)) {
            throw "Application not found: $Name"
        }
        return (Resolve-Path -LiteralPath $Name).Path
    }

    $command = Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $command) {
        throw "Could not resolve application '$Name'. Pass an exact path instead."
    }

    return $command.Source
}

$resolvedDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
$resolvedApplication = Resolve-Application -Name $Application
$resolvedLog = [System.IO.Path]::GetFullPath($LogPath)

if ($JobMemoryMiB -lt $ProcessMemoryMiB) { throw 'Job memory cap must be at least the process cap.' }

# BoundedProcess.Run opens the log with CreateFile(CREATE_ALWAYS), which fails if the
# directory does not exist. Create it before starting the process.
$logDirectory = [System.IO.Path]::GetDirectoryName($resolvedLog)
if ($logDirectory) {
    [void][System.IO.Directory]::CreateDirectory($logDirectory)
}

if (-not ('BoundedProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'BoundedProcess.cs') }

$previousHeapLimit = $env:DOTNET_GCHeapHardLimit
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$previousTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
try {
    # Managed heap ceiling for .NET processes only; supplements the kernel caps.
    $heapBytes = [long]($ProcessMemoryMiB * 0.75 * 1MB)
    $env:DOTNET_GCHeapHardLimit = $heapBytes.ToString('X')
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    $result = [BoundedProcess]::Run(
        $resolvedApplication, $Arguments, $resolvedDirectory, $resolvedLog,
        $TimeoutSeconds, $ProcessMemoryMiB, $JobMemoryMiB)

    $result | ConvertTo-Json | Set-Content -LiteralPath ($resolvedLog + '.json') -Encoding utf8
    $result

    if ($result.ExitCode -ne 0 -or $result.StopReason -ne 'completed') {
        throw "Bounded run failed: $($result.StopReason), exit $($result.ExitCode). See $resolvedLog"
    }
}
finally {
    $env:DOTNET_GCHeapHardLimit = $previousHeapLimit
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetry
}