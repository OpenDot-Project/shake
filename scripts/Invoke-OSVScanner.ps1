# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$scannerVersion = 'v2.7.0'
$scannerName = 'osv-scanner_windows_amd64.exe'
$downloadRoot = "https://github.com/google/osv-scanner/releases/download/$scannerVersion"
$runnerTemp = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { [System.IO.Path]::GetTempPath() } else { $env:RUNNER_TEMP }
$scannerPath = Join-Path $runnerTemp "osv-scanner-$scannerVersion.exe"
$checksumPath = Join-Path $runnerTemp "osv-scanner-$scannerVersion-SHA256SUMS"
$lockFiles = @(
    'src/OpenDot.Shake.Cli/packages.lock.json',
    'src/OpenDot.Shake.RubyHost/packages.lock.json'
)

foreach ($lockFile in $lockFiles) {
    $path = Join-Path $repositoryRoot $lockFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required NuGet lockfile was not found: $path"
    }
}

Invoke-WebRequest -Uri "$downloadRoot/$scannerName" -OutFile $scannerPath
Invoke-WebRequest -Uri "$downloadRoot/osv-scanner_SHA256SUMS" -OutFile $checksumPath

$checksumPattern = "^([0-9a-fA-F]{64})\s+$([regex]::Escape($scannerName))$"
$checksumLine = Get-Content -LiteralPath $checksumPath |
    Where-Object { $_ -match $checksumPattern } |
    Select-Object -First 1
if (-not $checksumLine) {
    throw "No SHA-256 checksum was published for $scannerName."
}

$expectedHash = [regex]::Match($checksumLine, $checksumPattern).Groups[1].Value.ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $scannerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "OSV-Scanner $scannerVersion SHA-256 verification failed."
}

$scannerArguments = @('scan', 'source', '--verbosity=warn')
foreach ($lockFile in $lockFiles) {
    $scannerArguments += @('--lockfile', (Join-Path $repositoryRoot $lockFile))
}

& $scannerPath @scannerArguments
if ($LASTEXITCODE -ne 0) {
    throw "OSV-Scanner exited with code $LASTEXITCODE."
}
