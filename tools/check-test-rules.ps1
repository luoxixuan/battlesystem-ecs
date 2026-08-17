<#
用法：pwsh -File tools\check-test-rules.ps1

作用（不依赖 dotnet）：
  1. 扫描 BattleSystemECS.Tests 下全部 *.cs（自动排除 bin/obj）。
  2. 按行解析每个 [Fact] / [Theory] 方法体（行级大括号平衡），
     方法体内必须至少出现一次 Assert.；否则输出 文件:行:测试名。
  3. grep 恒真/恒假断言：
       Assert.True(true) / Assert.False(false) /
       Assert.True(false) / Assert.False(true)
     发现即输出 文件:行:模式。
  4. 发现任意违规 exit 1；0 违规 exit 0。

注意：只做静态规则检查，不会构建或运行测试。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repoRoot 'BattleSystemECS.Tests'
if (-not (Test-Path -LiteralPath $testRoot -PathType Container)) {
    Write-Error "找不到测试目录：$testRoot"
    exit 2
}

$files = @(Get-ChildItem -LiteralPath $testRoot -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })

$zeroAssertViolations = @()
$constantViolations = @()
$totalMethods = 0

# ── 恒真 / 恒假四模式 ──
$constantPatterns = @(
    @{ Name = 'Assert.True(true)';  Regex = 'Assert\.True\s*\(\s*true\s*\)' },
    @{ Name = 'Assert.False(false)'; Regex = 'Assert\.False\s*\(\s*false\s*\)' },
    @{ Name = 'Assert.True(false)'; Regex = 'Assert\.True\s*\(\s*false\s*\)' },
    @{ Name = 'Assert.False(true)'; Regex = 'Assert\.False\s*\(\s*true\s*\)' }
)

function Get-RelPath {
    param([string]$FullPath)
    return $FullPath.Substring($repoRoot.Length + 1)
}

<#
逐行扫描方法体（跨行维护字符串 / 注释状态）：
  - 只统计代码区（排除字符串、char 字面量、注释）中的大括号与 Assert.；
  - 行级大括号平衡：Depth 归 0 表示方法体结束。
#>
function Scan-TestLine {
    param(
        [hashtable]$State,
        [string]$Line,
        [int]$Depth
    )

    $inBlock = [bool]$State.InBlock
    $inString = [bool]$State.InString
    $inVerbatim = [bool]$State.InVerbatim
    $escaped = [bool]$State.Escaped
    $hasAssert = $false
    $depth = $Depth
    $chars = $Line.ToCharArray()
    $n = $chars.Length

    for ($pos = 0; $pos -lt $n; $pos++) {
        $c = $chars[$pos]

        if ($inBlock) {
            if ($c -eq '*' -and ($pos + 1) -lt $n -and $chars[$pos + 1] -eq '/') {
                $inBlock = $false
                $pos++
            }
            continue
        }

        if ($inVerbatim) {
            if ($c -eq '"') {
                if (($pos + 1) -lt $n -and $chars[$pos + 1] -eq '"') {
                    $pos++
                    continue
                }
                $inVerbatim = $false
            }
            continue
        }

        if ($inString) {
            if ($escaped) { $escaped = $false; continue }
            if ($c -eq '\') { $escaped = $true; continue }
            if ($c -eq '"') { $inString = $false }
            continue
        }

        # ── 代码区 ──
        if ($c -eq '/' -and ($pos + 1) -lt $n) {
            if ($chars[$pos + 1] -eq '/') { break }              # 行注释：本行剩余全部跳过
            if ($chars[$pos + 1] -eq '*') { $inBlock = $true; $pos++; continue }
        }

        if ($c -eq '@' -and ($pos + 1) -lt $n -and $chars[$pos + 1] -eq '"') {
            $inVerbatim = $true
            $pos++
            continue
        }

        if ($c -eq '"') { $inString = $true; continue }

        if ($c -eq "'") {                                       # char 字面量
            $pos++
            while ($pos -lt $n) {
                if ($chars[$pos] -eq '\') { $pos++ }
                elseif ($chars[$pos] -eq "'") { break }
                $pos++
            }
            continue
        }

        if ($c -eq '{') { $depth++ }
        elseif ($c -eq '}') {
            $depth--
            if ($depth -le 0) { break }
        }

        if (-not $hasAssert -and $c -eq 'A' -and ($pos + 6) -lt $n) {
            if ($Line.Substring($pos, 7) -ceq 'Assert.') { $hasAssert = $true }
        }
    }

    $State.InBlock = $inBlock
    $State.InString = $inString
    $State.InVerbatim = $inVerbatim
    $State.Escaped = $escaped
    return @{ Depth = $depth; HasAssert = $hasAssert }
}

foreach ($file in $files) {
    $lines = @(Get-Content -LiteralPath $file.FullName)
    $relPath = Get-RelPath -FullPath $file.FullName

    # ── 恒真 / 恒假断言：按行 grep（含注释中的残留也一并报出）──
    for ($li = 0; $li -lt $lines.Count; $li++) {
        foreach ($pattern in $constantPatterns) {
            if ($lines[$li] -match $pattern.Regex) {
                $constantViolations += ('{0}:{1}: {2}' -f $relPath, ($li + 1), $pattern.Name)
            }
        }
    }

    # ── [Fact] / [Theory] 方法体解析 ──
    $i = 0
    while ($i -lt $lines.Count) {
        $trim = $lines[$i].TrimStart()
        if ($trim.StartsWith('//') -or $trim.StartsWith('/*') -or $trim.StartsWith('*')) { $i++; continue }

        if ($trim -notmatch '^\[\s*(Fact|Theory)\b') { $i++; continue }

        $attrLineIndex = $i
        $sigLineIndex = -1
        $sigLine = $null

        # 属性与方法签名同行：[Fact] public void Xxx() { ... }
        $sameLine = [regex]::Match($lines[$i], '^\s*\[\s*(?:Fact|Theory)\b[^\]]*\]\s*(?<sig>.+)$')
        if ($sameLine.Success -and $sameLine.Groups['sig'].Value -match '\(') {
            $sigLineIndex = $i
            $sigLine = $lines[$i]
        }
        else {
            # 跳过 InlineData 等多行属性，找到真正的声明行
            $limit = $i + 60
            if ($limit -gt $lines.Count) { $limit = $lines.Count }
            for ($j = $i + 1; $j -lt $limit; $j++) {
                $t = $lines[$j].TrimStart()
                if ($t -eq '') { continue }
                if ($t.StartsWith('[')) { continue }
                if ($t.StartsWith('//') -or $t.StartsWith('/*') -or $t.StartsWith('*')) { continue }
                if ($t -match '^(InlineData|MemberData|ClassData|DynamicData)\b') { continue }
                if ($t.TrimEnd().EndsWith(')]')) { continue }

                if ($t -match '\(') {
                    $nameCheck = [regex]::Match($t, '([A-Za-z_][A-Za-z0-9_]*)(?:\s*<[^>]*>)?\s*\(')
                    if ($nameCheck.Success) {
                        $sigLineIndex = $j
                        $sigLine = $lines[$j]
                        break
                    }
                }
            }
        }

        if ($sigLineIndex -lt 0) { $i++; continue }

        $nameMatch = [regex]::Match($sigLine, '([A-Za-z_][A-Za-z0-9_]*)(?:\s*<[^>]*>)?\s*\(')
        $testName = if ($nameMatch.Success) { $nameMatch.Groups[1].Value } else { '<unknown>' }

        # ── 定位方法体起点 ──
        $bodyIndex = -1
        $expressionBody = $false
        if ($sigLine -match '\{') {
            $bodyIndex = $sigLineIndex
        }
        else {
            $look = $sigLineIndex + 1
            $limit = $sigLineIndex + 40
            if ($limit -gt $lines.Count) { $limit = $lines.Count }
            while ($look -lt $limit) {
                $t = $lines[$look].TrimStart()
                if ($t -eq '' -or $t.StartsWith('//') -or $t.StartsWith('/*') -or $t.StartsWith('*')) { $look++; continue }
                if ($t.StartsWith('{')) { $bodyIndex = $look; break }
                if ($t.StartsWith('=>')) { $expressionBody = $true; $bodyIndex = $look; break }
                # 参数续行（多行方法签名）：继续向后找方法体
                $look++
            }
            if ($bodyIndex -lt 0 -and $sigLine -match '=>') {
                $expressionBody = $true
                $bodyIndex = $sigLineIndex
            }
        }

        if ($bodyIndex -ge 0) {
            $totalMethods++
            $hasAssert = $false
            $advanced = $false

            if ($expressionBody) {
                # 表达式体：从 => 之后开始，按行找代码直到以 ; 收尾
                $depth = 0
                for ($k = $bodyIndex; $k -lt $lines.Count; $k++) {
                    $lineText = $lines[$k]
                    $afterArrow = ''
                    if ($k -eq $bodyIndex) {
                        $idx = $lineText.IndexOf('=>')
                        if ($idx -ge 0) { $afterArrow = $lineText.Substring($idx + 2) }
                    }
                    else {
                        $afterArrow = $lineText
                    }
                    # 去掉行注释后检查 Assert.
                    $codeOnly = [regex]::Replace($afterArrow, '//[^\r\n]*', '')
                    if ($codeOnly -match 'Assert\.') { $hasAssert = $true }
                    $codeOnly = $codeOnly.TrimEnd()
                    if ($codeOnly.EndsWith(';')) { break }
                }
            }
            else {
                $depth = 0
                $state = @{ InBlock = $false; InString = $false; InVerbatim = $false; Escaped = $false }
                for ($k = $bodyIndex; $k -lt $lines.Count; $k++) {
                    $r = Scan-TestLine -State $state -Line $lines[$k] -Depth $depth
                    $depth = $r.Depth
                    if ($r.HasAssert) { $hasAssert = $true }
                    if ($depth -le 0) { $i = $k + 1; $advanced = $true; break }
                }
            }

            if (-not $hasAssert) {
                $zeroAssertViolations += ('{0}:{1}:{2}' -f $relPath, ($attrLineIndex + 1), $testName)
            }
        }

        if (-not $advanced) { $i++ }
    }
}

# ── 中文摘要 ──
Write-Output '══ 测试静态规则检查 ══'
Write-Output ("已扫描文件：{0} 个" -f $files.Count)
Write-Output ("已解析测试方法：[Fact]/[Theory] 共 {0} 个" -f $totalMethods)
Write-Output ("零断言测试：{0} 处" -f $zeroAssertViolations.Count)
foreach ($v in $zeroAssertViolations) { Write-Output ("  [零断言] " + $v) }
Write-Output ("恒真/恒假断言：{0} 处" -f $constantViolations.Count)
foreach ($v in $constantViolations) { Write-Output ("  [恒真/恒假] " + $v) }

if ($zeroAssertViolations.Count -gt 0 -or $constantViolations.Count -gt 0) {
    Write-Output '结论：发现违规，静态规则检查未通过。'
    exit 1
}

Write-Output '结论：0 违规，静态规则检查通过。'
exit 0
