param([string]$EvidenceRoot = 'C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m7-installer-registration-final-20260902T231500Z')
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $EvidenceRoot, (Join-Path $EvidenceRoot 'attempt-failures') | Out-Null
Get-ChildItem (Join-Path $EvidenceRoot 'attempt-failures') -File -ErrorAction SilentlyContinue | Remove-Item -Force
$records = [System.Collections.Generic.List[object]]::new()
function Run-Gate([string]$Name,[string]$Command,[int]$SemanticExit=0) {
  $start=[DateTimeOffset]::Now; $out=Join-Path $EvidenceRoot ($Name+'.stdout.log'); $err=Join-Path $EvidenceRoot ($Name+'.stderr.log')
  & pwsh -NoProfile -Command $Command 1> $out 2> $err; $exit=$LASTEXITCODE; $end=[DateTimeOffset]::Now
  $semantic=if($exit -eq $SemanticExit){'PASS'}else{'FAIL'}
  $result=if($semantic -eq 'PASS'){'PASS'}else{'FAIL'}
  $stdoutHash=(Get-FileHash $out -Algorithm SHA256).Hash; $stderrHash=(Get-FileHash $err -Algorithm SHA256).Hash
  $hashInput="$stdoutHash`n$stderrHash`n$result"; $hashBytes=[System.Text.Encoding]::UTF8.GetBytes($hashInput)
  $hash=[System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($hashBytes)).Replace('-','')
  $records.Add([pscustomobject]@{name=$Name;command=$Command;cwd=(Get-Location).Path;startUtc=$start.UtcDateTime.ToString('o');endUtc=$end.UtcDateTime.ToString('o');startLocal=$start.ToString('o');endLocal=$end.ToString('o');exitCode=$exit;semanticStatus=$semantic;result=$result;stdout=$out;stderr=$err;stdoutSha256=$stdoutHash;stderrSha256=$stderrHash;hash=$hash;fresh=$true})
  if($semantic -eq 'FAIL'){Copy-Item $out (Join-Path $EvidenceRoot 'attempt-failures') -Force; Copy-Item $err (Join-Path $EvidenceRoot 'attempt-failures') -Force}
}
Run-Gate 'engine-build' 'dotnet build BattleSystemECS.Engine --nologo'
Run-Gate 'core-build' 'dotnet build BattleSystemECS.Core --nologo'
Run-Gate 'exe-build' 'dotnet build --nologo'
Run-Gate 'tests-build' 'dotnet build BattleSystemECS.Tests --nologo'
Run-Gate 'focused-tests' 'dotnet test BattleSystemECS.Tests --no-build --nologo --filter "FullyQualifiedName~FrameGraphProductionFlowTests|FullyQualifiedName~SystemInstallerProductionFlowTests"'
Run-Gate 'full-tests' 'dotnet test BattleSystemECS.Tests --no-build --nologo'
Run-Gate 'test-rules' 'pwsh -File tools/check-test-rules.ps1'
Run-Gate 'diff-check' 'git diff --check'
Run-Gate 'generator-root-1' 'pwsh -File tools/generate-system-registry-ledger.ps1 -Source Core/SystemRegistry.cs -Spec tools/system-registration-spec.json -Output docs/ecs-gas-m7-nullable-ledger.md -ManifestOutput Core/SystemRegistrationManifest.generated.cs'
$rootA=Join-Path $EvidenceRoot 'gen-a'; $rootB=Join-Path $EvidenceRoot 'gen-b'; New-Item -ItemType Directory -Force $rootA,$rootB | Out-Null
Run-Gate 'generator-cross-root-a' "pwsh -File tools/generate-system-registry-ledger.ps1 -Source Core/SystemRegistry.cs -Spec tools/system-registration-spec.json -Output '$rootA/ledger.md' -ManifestOutput '$rootA/manifest.cs'"
Run-Gate 'generator-cross-root-b' "pwsh -File tools/generate-system-registry-ledger.ps1 -Source Core/SystemRegistry.cs -Spec tools/system-registration-spec.json -Output '$rootB/ledger.md' -ManifestOutput '$rootB/manifest.cs'"
Run-Gate 'generator-cross-root-compare' "if((Get-FileHash '$rootA/ledger.md').Hash -ne (Get-FileHash '$rootB/ledger.md').Hash){exit 1}; if((Get-FileHash '$rootA/manifest.cs').Hash -ne (Get-FileHash '$rootB/manifest.cs').Hash){exit 1}"
Run-Gate 'recursive-il-scan' 'dotnet test BattleSystemECS.Tests --no-build --nologo --filter "FullyQualifiedName~ManifestDependenciesExactlyMatchTypedRecipeIl|FullyQualifiedName~RecipeDependencyWalkerFollowsCompilerGeneratedClosureBodies"'
Run-Gate 'node-scan' 'rg -n "FrameNodeDefinition|IFrameNode|FrameGraphBuilder|FrameNodeAdapter" BattleSystemECS.Engine Core BattleSystemECS.Tests'
Run-Gate 'registration-scan' 'rg -n "schemaVersion|RegistrationStage|ProductionSystemInstaller|FrameBindingFacts" tools Core BattleSystemECS.Tests'
Run-Gate 'content-boundary-scan' 'rg -n "BattleSystemECS\.Systems\." Core/ContentContracts.cs BattleSystemECS.Engine' 1
Run-Gate 'phase-scan' ' $files=@("tools/capture-m7-fresh-evidence.ps1","README.md","AGENTS.md") + @(Get-ChildItem docs,Research -Recurse -File | ForEach-Object FullName); $patterns=@(("223000"+"Z"),("235500"+"Z"),("200000"+"Z"),("183500"+"Z"),("131143"+"Z"),("85139"+"a4"),"旧目录","旧 topology","review-root","DLL snapshot","historical hash","old performance",("17"+"60"), ("17"+"61")); $hits=$files | Select-String -Pattern $patterns; if($hits){$hits | ForEach-Object { $_.ToString() }; exit 1 }'
Run-Gate 'binding-scan' 'rg -n "unknownNoOp|RegisterLegacyFrameBindings|RegisterFrameBinding\(\s*\"" Core/FrameScheduler.cs Core/FrameSystemGraph.cs Core/SystemRegistry.cs Core/FrameBindingFacts.cs BattleSystemECS.Tests/Integration/FrameGraphProductionFlowTests.cs' 1
Run-Gate 'bufftype-scan' 'Get-FileHash Core/BuffType.cs -Algorithm SHA256'
$index=Join-Path $EvidenceRoot 'index.txt'; git ls-files -s | Out-File $index -Encoding utf8; (Get-FileHash $index -Algorithm SHA256).Hash | Out-File (Join-Path $EvidenceRoot 'index-sha256') -Encoding ascii
$head=git rev-parse HEAD; $status=(& git status --short | Out-String); $tracked=git diff --binary; $tracked | Out-File (Join-Path $EvidenceRoot 'tracked.patch') -Encoding utf8; $indexHash=(Get-FileHash $index -Algorithm SHA256).Hash; $cachedFiles=@(git diff --cached --name-only); $indexStatus=if($cachedFiles.Count -eq 0){'CLEAN'}else{'DIRTY'}; $patchHash=(Get-FileHash (Join-Path $EvidenceRoot 'tracked.patch') -Algorithm SHA256).Hash; $inventory=@("HEAD=$head","INDEX_SHA256=$indexHash","INDEX_STATUS=$indexStatus","TRACKED_PATCH_SHA256=$patchHash","STATUS_BEGIN"); if($status.Length -gt 0){ $inventory += $status.TrimEnd("`r","`n") -split "`r?`n" }; $inventory += "STATUS_END"; git ls-files --others --exclude-standard | ForEach-Object { $inventory += ("$_`tSHA256="+((Get-FileHash $_ -Algorithm SHA256).Hash)) }; $inventory | Out-File (Join-Path $EvidenceRoot 'dirty-inventory.txt') -Encoding utf8
Run-Gate 'dirty-inventory-schema' "`$inventory=Get-Content '$(Join-Path $EvidenceRoot 'dirty-inventory.txt')'; `$begin=[Array]::IndexOf(`$inventory,'STATUS_BEGIN'); `$end=[Array]::IndexOf(`$inventory,'STATUS_END'); if(`$begin -lt 0 -or `$end -le `$begin){ Write-Error 'dirty-inventory is missing an ordered STATUS_BEGIN/STATUS_END block'; exit 1 }; `$actual=(((& git status --short | Out-String) -split ""`r?`n"" | Where-Object { `$_.Length -gt 0 }) -join ""`n""); `$captured=if(`$end -eq `$begin+1){''}else{(`$inventory[(`$begin+1)..(`$end-1)] -join ""`n"")}; if(`$captured -ne `$actual){ Write-Error 'dirty-inventory status block does not match git status --short'; exit 1 }"
foreach($record in $records) {
  if([string]::IsNullOrWhiteSpace($record.hash)) { throw "Evidence record '$($record.name)' has no hash." }
  $checkInput="$($record.stdoutSha256)`n$($record.stderrSha256)`n$($record.result)"; $checkBytes=[System.Text.Encoding]::UTF8.GetBytes($checkInput)
  $checkHash=[System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($checkBytes)).Replace('-','')
  if($checkHash -ne $record.hash) { throw "Evidence record '$($record.name)' has an invalid hash." }
}
$records | ConvertTo-Json -Depth 5 | Out-File (Join-Path $EvidenceRoot 'command-manifest.json') -Encoding utf8
$hashFile=Join-Path $EvidenceRoot 'evidence-sha256'; Get-ChildItem $EvidenceRoot -File -Recurse | Where-Object {$_.FullName -ne $hashFile} | Get-FileHash -Algorithm SHA256 | Sort-Object Path | ForEach-Object { $_.Hash+'  '+$_.Path } | Out-File $hashFile -Encoding ascii
