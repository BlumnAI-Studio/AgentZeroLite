# find-cli.ps1 — Locate AgentZeroLite.exe by priority, verify build identity.
#
# The official host is the Avalonia one (Project/AgentZeroAvalonia); WPF is the reference/lab
# build. Both are called AgentZeroLite.exe and speak the same -cli JSON, but each exe talks
# only to ITS OWN kind of GUI: a Debug Avalonia exe uses the pipe AgentZeroLite.cli.debug
# (the Debug GUI), a Release / installed / Store exe the pipe AgentZeroLite.cli, and the WPF
# exe WM_COPYDATA. So the best answer is the exe of the GUI that is running right now.
#
# Priority:
#   0. The exe of a running AgentZeroLite GUI (newest first; Debug preferred when several run)
#   1. Avalonia Debug build   2. Avalonia Release build
#   3. Installed (%ProgramFiles%\AgentZeroLite — the Avalonia installer keeps the old path)
#   4. Microsoft Store package (webnori.AgentZeroLite)
#   5. WPF Debug / Release build (reference host)   6. PATH
#
# Prints the resolved exe path to stdout (last line) so callers can capture it:
#   $Exe = (& .claude/skills/agentzero-cli/scripts/find-cli.ps1 -Quiet)
#
# Options:
#   -RepoRoot <path>   Repo root to search under (default: git top-level, else CWD)
#   -HostKind <kind>   any (default) | avalonia | wpf — restrict to one host's builds
#   -NoRunning         Ignore running GUIs; resolve by build priority only
#   -Quiet             Print only the exe path (no identity banner)

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [ValidateSet('any', 'avalonia', 'wpf')][string]$HostKind = 'any',
    [switch]$NoRunning,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = (git rev-parse --show-toplevel 2>$null)
    if (-not $RepoRoot) { $RepoRoot = (Get-Location).Path }
}

function Get-HostOf([string]$path) {
    if ($path -match '\\AgentZeroWpf\\|net10\.0-windows\\AgentZeroLite\.exe$|AgentZeroLite-WPF') { 'wpf' } else { 'avalonia' }
}

$exe = $null

# 0. A running GUI answers to its own exe.
if (-not $NoRunning) {
    $running = Get-Process AgentZeroLite -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($HostKind -eq 'any' -or (Get-HostOf $_.Path) -eq $HostKind) } |
        Sort-Object @{ Expression = { $_.Path -like '*\bin\Debug\*' }; Descending = $true }, @{ Expression = 'StartTime'; Descending = $true }
    if ($running) { $exe = @($running)[0].Path }
}

if (-not $exe) {
    $avalonia = @(
        (Join-Path $RepoRoot 'Project\AgentZeroAvalonia\bin\Debug\net10.0\AgentZeroLite.exe'),
        (Join-Path $RepoRoot 'Project\AgentZeroAvalonia\bin\Release\net10.0\AgentZeroLite.exe'),
        (Join-Path $env:ProgramFiles 'AgentZeroLite\AgentZeroLite.exe')
    )
    $store = Get-AppxPackage -Name 'webnori.AgentZeroLite' -ErrorAction SilentlyContinue |
        Select-Object -First 1 | ForEach-Object { Join-Path $_.InstallLocation 'AgentZeroLite.exe' }
    $wpf = @(
        (Join-Path $RepoRoot 'Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe'),
        (Join-Path $RepoRoot 'Project\AgentZeroWpf\bin\Release\net10.0-windows\AgentZeroLite.exe')
    )
    $candidates = switch ($HostKind) {
        'avalonia' { $avalonia + @($store) }
        'wpf'      { $wpf }
        default    { $avalonia + @($store) + $wpf }
    }
    $exe = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

if (-not $exe) {
    $onPath = Get-Command AgentZeroLite.exe -ErrorAction SilentlyContinue
    if ($onPath) { $exe = $onPath.Source }
}

if (-not $exe) {
    Write-Error "AgentZeroLite.exe not found. Build it (dotnet build Project/AgentZeroAvalonia) or install it."
    exit 1
}

$exe = (Resolve-Path $exe).Path

if (-not $Quiet) {
    $which = switch -Wildcard ($exe) {
        '*\bin\Debug\*'     { 'DEBUG build (internal test — talks to the Debug GUI only)'; break }
        '*\bin\Release\*'   { 'RELEASE build'; break }
        '*\WindowsApps\*'   { 'STORE package'; break }
        "*$env:ProgramFiles*" { 'INSTALLED build'; break }
        default { 'PATH / other' }
    }
    $isRunning = [bool](Get-Process AgentZeroLite -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    Write-Host "AgentZeroLite.exe → $((Get-HostOf $exe).ToUpper()) · $which$(if ($isRunning) { ' · GUI running' })" -ForegroundColor Cyan
    Write-Host "  $exe" -ForegroundColor DarkGray
    # Verify identity (needs no GUI). Start-Process because it is a WinExe.
    Start-Process $exe -ArgumentList '-cli','version' -NoNewWindow -Wait
}

# Last line = the path, for capture.
$exe
