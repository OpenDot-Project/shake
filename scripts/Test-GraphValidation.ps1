# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repositoryRoot "src/OpenDot.Shake.Cli/bin/$Configuration/net10.0-windows/OpenDot.Shake.Cli.exe"
if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) {
    throw "Shake CLI was not built at the expected path: $cliPath"
}

$ruby = Get-Command ruby -ErrorAction Stop
$rubyBin = Split-Path -Parent $ruby.Source
$env:SHAKE_RUBY_HOME = Split-Path -Parent $rubyBin
$tempRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) "shake-graph-smoke-$([guid]::NewGuid().ToString('N'))"))
$tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempPrefix = if ($tempParent.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $tempParent } else { $tempParent + [System.IO.Path]::DirectorySeparatorChar }
if (-not $tempRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The smoke-test workspace must be contained in the system temporary directory.'
}

New-Item -ItemType Directory -Path $tempRoot | Out-Null

function New-Workspace([string] $Name, [string] $RootBuild, [hashtable] $NestedBuilds = @{}) {
    $workspace = Join-Path $tempRoot $Name
    New-Item -ItemType Directory -Path $workspace | Out-Null
    Set-Content -LiteralPath (Join-Path $workspace 'MODULE.rb') -Value '# graph smoke workspace' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $workspace 'BUILD.rb') -Value $RootBuild -Encoding utf8NoBOM

    foreach ($relativePath in $NestedBuilds.Keys) {
        $buildPath = Join-Path $workspace $relativePath
        $buildDirectory = Split-Path -Parent $buildPath
        New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
        Set-Content -LiteralPath $buildPath -Value $NestedBuilds[$relativePath] -Encoding utf8NoBOM
    }

    return $workspace
}

function Invoke-Graph([string] $Workspace, [string[]] $Arguments) {
    Push-Location $Workspace
    try {
        $output = & $cliPath graph @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $exitCode
            Text = ($output -join [Environment]::NewLine)
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-GraphResult([string] $Name, [pscustomobject] $Result, [int] $ExpectedExitCode, [string] $ExpectedText) {
    if ($Result.ExitCode -ne $ExpectedExitCode) {
        throw "$Name expected exit $ExpectedExitCode but got $($Result.ExitCode): $($Result.Text)"
    }

    if ($Result.Text -notmatch [regex]::Escape($ExpectedText)) {
        throw "$Name output did not contain '$ExpectedText': $($Result.Text)"
    }

    Write-Host "PASS $Name"
}

try {
    $validWorkspace = New-Workspace 'valid' @'
cc_binary "tool" do |target|
  target.sources "tool.c"
end

genrule "emit" do |target|
  target.outputs "generated.c"
  target.tool ":tool"
end

default_targets ":emit"
'@
    $valid = Invoke-Graph $validWorkspace @('//:emit', '--format', 'json')
    Assert-GraphResult 'valid graph' $valid 0 'generated.c'
    $graphJson = $valid.Text | ConvertFrom-Json
    $generated = $graphJson.targets | Where-Object label -eq '//:emit'
    if ($generated.tools -notcontains '//:tool' -or $generated.outputs -notcontains 'generated.c') {
        throw 'valid graph JSON omitted the normalized tool or output field.'
    }

    $duplicateDefaultsWorkspace = New-Workspace 'duplicate-defaults' @'
cc_binary "app" do |target|
  target.sources "main.c"
end

default_targets ":app", "//:app"
'@
    $duplicateDefaults = Invoke-Graph $duplicateDefaultsWorkspace @('//:app')
    Assert-GraphResult 'duplicate default targets' $duplicateDefaults 2 'duplicate'

    $duplicateOutputsWorkspace = New-Workspace 'duplicate-outputs' @'
cc_binary "tool" do |target|
  target.sources "tool.c"
end

genrule "emit_a" do |target|
  target.outputs "generated.c"
  target.tool ":tool"
end

genrule "emit_b" do |target|
  target.outputs "generated.c"
  target.tool ":tool"
end
'@
    $duplicateOutputs = Invoke-Graph $duplicateOutputsWorkspace @('//:emit_a')
    Assert-GraphResult 'duplicate output ownership' $duplicateOutputs 2 'conflicts with output'

    $pathTraversalWorkspace = New-Workspace 'path-traversal' @'
cc_binary "app" do |target|
  target.sources "../outside.c"
end
'@
    $pathTraversal = Invoke-Graph $pathTraversalWorkspace @('//:app')
    Assert-GraphResult 'path traversal rejection' $pathTraversal 2 'escapes its package directory'

    $cycleWorkspace = New-Workspace 'cycle' @'
filegroup "left" do |target|
  target.sources "left.txt"
  target.dependencies ":right"
end

filegroup "right" do |target|
  target.sources "right.txt"
  target.dependencies ":left"
end
'@
    $cycle = Invoke-Graph $cycleWorkspace @('//:left')
    Assert-GraphResult 'dependency cycle rejection' $cycle 2 'dependency cycle detected'

    $visibilityWorkspace = New-Workspace 'visibility' @'
cc_library "core" do |target|
  target.sources "core.c"
end
'@ @{
        'client/BUILD.rb' = @'
cc_binary "app" do |target|
  target.sources "main.c"
  target.dependencies "//:core"
end
'@
    }
    $visibility = Invoke-Graph $visibilityWorkspace @('//client:app')
    Assert-GraphResult 'private dependency rejection' $visibility 2 'not visible'
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
