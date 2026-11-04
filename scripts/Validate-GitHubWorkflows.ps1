# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repositoryRoot

try {
    $actionlint = Get-Command actionlint -ErrorAction Stop
    & $actionlint.Source -color
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $zizmor = Get-Command zizmor -ErrorAction Stop
    & $zizmor.Source --strict-collection --format=plain --collect=workflows -- .github
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & $zizmor.Source --strict-collection --format=plain --collect=dependabot -- .github/dependabot.yml
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
