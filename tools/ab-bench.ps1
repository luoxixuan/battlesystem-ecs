#requires -Version 7
<#
.SYNOPSIS
    新旧构建 A/B 压测：在同一 cwd 交替运行两份 Release 产物，输出各 mode 的 FPS 中位数与 mode 4 逐节点中位数。

.DESCRIPTION
    A = 指定 git 引用（默认 HEAD，经临时 worktree 构建，不触碰当前工作树）；B = 当前工作树。
    两份产物都从仓库根目录运行（game_config.json 按 cwd 读取，两边读同一份配置），并按
    A,B / B,A 交替排列，抵消"先跑者偏快"的顺序偏差。mode 2/4 走 stdin 菜单，mode 5 走命令行参数。
    任一次 mode 2/4 输出 valid=FALSE 时在汇总中标注，该构建 FPS 不应作为 10K 负载结论。

.EXAMPLE
    pwsh -File tools/ab-bench.ps1
    pwsh -File tools/ab-bench.ps1 -Base HEAD~1 -Modes 4 -Pairs 3
    pwsh -File tools/ab-bench.ps1 -Modes 5 -Mode5Pairs 12
#>
param(
    [string]$Base = 'HEAD',
    [int[]]$Modes = @(2, 4, 5),
    [int]$Pairs = 2,
    [int]$Mode5Pairs = 8,
    [int]$TopNodes = 10,
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'battlesystem-ab-bench')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worktree = Join-Path $WorkDir 'base-src'
$binA = Join-Path $WorkDir 'binA'
$binB = Join-Path $WorkDir 'binB'

function Invoke-Build([string]$project, [string]$output) {
    $log = dotnet build $project -c Release -o $output 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 20 | Write-Host; throw "build failed: $project" }
}

function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) { return [double]::NaN }
    $sorted = $values | Sort-Object
    $mid = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

function Invoke-Mode([string]$bin, [int]$mode) {
    $dll = Join-Path $bin 'BattleSystemECS.dll'
    Push-Location $repo
    try {
        if ($mode -eq 5) { $out = dotnet $dll 5 2>&1 | Out-String }
        else { $out = "$mode" | dotnet $dll 2>&1 | Out-String }
    } finally { Pop-Location }
    $fps = [double]::NaN
    $m = [regex]::Match($out, 'Throughput:\s+([0-9.]+)\s+FPS')
    if ($m.Success) { $fps = [double]$m.Groups[1].Value }
    $nodes = @{}
    foreach ($nm in [regex]::Matches($out, 'node\s+(\S+)\s+([0-9.]+)\s+ms')) { $nodes[$nm.Groups[1].Value] = [double]$nm.Groups[2].Value }
    [pscustomobject]@{
        Fps     = $fps
        Invalid = $out -match 'valid=FALSE'
        End     = ([regex]::Match($out, 'End reason:\s+(\S+)')).Groups[1].Value
        Frames  = ([regex]::Match($out, 'Total frames:\s+(\d+)')).Groups[1].Value
        Nodes   = $nodes
    }
}

New-Item -ItemType Directory -Force $WorkDir | Out-Null
try {
    Write-Host "[AB] base=$Base  -> A ; working tree -> B ; workdir=$WorkDir"
    if (Test-Path $worktree) { git -C $repo worktree remove --force $worktree 2>$null | Out-Null }
    git -C $repo worktree add --detach $worktree $Base | Out-Null
    Invoke-Build (Join-Path $worktree 'BattleSystemECS.csproj') $binA
    Invoke-Build (Join-Path $repo 'BattleSystemECS.csproj') $binB

    foreach ($mode in $Modes) {
        $n = if ($mode -eq 5) { $Mode5Pairs } else { $Pairs }
        $runs = @{ A = @(); B = @() }
        for ($i = 0; $i -lt $n; $i++) {
            $order = if ($i % 2 -eq 0) { @('A', 'B') } else { @('B', 'A') }
            foreach ($which in $order) {
                $bin = if ($which -eq 'A') { $binA } else { $binB }
                $runs[$which] += Invoke-Mode $bin $mode
            }
        }
        Write-Host ""
        Write-Host "[AB] mode $mode ($n pairs, alternating)"
        foreach ($which in 'A', 'B') {
            $r = $runs[$which]
            $fps = @($r | ForEach-Object Fps)
            $flags = @()
            if ($r | Where-Object Invalid) { $flags += 'valid=FALSE seen' }
            $ends = ($r | Where-Object End | ForEach-Object { "$($_.End)/$($_.Frames)" } | Sort-Object -Unique) -join ','
            if ($ends) { $flags += "end=$ends" }
            Write-Host ("  {0}  median {1,7:F1} FPS   runs: {2}   {3}" -f $which, (Get-Median $fps), (($fps | ForEach-Object { '{0:F0}' -f $_ }) -join ' '), ($flags -join '; '))
        }
        if ($mode -eq 4) {
            $names = $runs['B'] | ForEach-Object { $_.Nodes.Keys } | Sort-Object -Unique
            $rows = foreach ($name in $names) {
                $a = Get-Median @($runs['A'] | ForEach-Object { if ($_.Nodes.ContainsKey($name)) { $_.Nodes[$name] } })
                $b = Get-Median @($runs['B'] | ForEach-Object { if ($_.Nodes.ContainsKey($name)) { $_.Nodes[$name] } })
                [pscustomobject]@{ Node = $name; A = $a; B = $b; Delta = $b - $a }
            }
            Write-Host "  node medians (ms per measured run), top $TopNodes by B:"
            $rows | Sort-Object B -Descending | Select-Object -First $TopNodes | ForEach-Object {
                Write-Host ("    {0,-48} A {1,8:F1}   B {2,8:F1}   delta {3,8:F1}" -f $_.Node, $_.A, $_.B, $_.Delta)
            }
        }
    }
}
finally {
    if (Test-Path $worktree) { git -C $repo worktree remove --force $worktree 2>$null | Out-Null }
}
