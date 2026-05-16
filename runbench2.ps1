$ErrorActionPreference = "Continue"
$proj = "F:\AI\BattleSystem-ECS"

# Mode 2
$pinfo2 = New-Object System.Diagnostics.ProcessStartInfo
$pinfo2.FileName = "dotnet"
$pinfo2.Arguments = "run -c Release"
$pinfo2.WorkingDirectory = $proj
$pinfo2.RedirectStandardInput = $true
$pinfo2.RedirectStandardOutput = $true
$pinfo2.RedirectStandardError = $true
$pinfo2.UseShellExecute = $false
$pinfo2.CreateNoWindow = $true
$p2 = New-Object System.Diagnostics.Process
$p2.StartInfo = $pinfo2
$p2.Start() | Out-Null
Start-Sleep -Milliseconds 500
$p2.StandardInput.WriteLine("2")
$p2.StandardInput.Close()
$stdout2 = $p2.StandardOutput.ReadToEnd()
$timedOut2 = -not $p2.WaitForExit(45000)
if ($timedOut_2) { $p2.Kill() }
Write-Host "=== MODE 2 ==="
$stdout2 | Select-Object -Last 20
if ($stdout2 -match 'Throughput:\s*(\d+)\s+FPS') { Write-Host "MODE2_FPS=$($Matches[1])" }
if ($stdout2 -match 'ms/frame.*?(\d+\.\d+)') { Write-Host "MODE2_ms_frame=$($Matches[1])" }

# Mode 4
$pinfo4 = New-Object System.Diagnostics.ProcessStartInfo
$pinfo4.FileName = "dotnet"
$pinfo4.Arguments = "run -c Release"
$pinfo4.WorkingDirectory = $proj
$pinfo4.RedirectStandardInput = $true
$pinfo4.RedirectStandardOutput = $true
$pinfo4.RedirectStandardError = $true
$pinfo4.UseShellExecute = $false
$pinfo4.CreateNoWindow = $true
$p4 = New-Object System.Diagnostics.Process
$p4.StartInfo = $pinfo4
$p4.Start() | Out-Null
Start-Sleep -Milliseconds 500
$p4.StandardInput.WriteLine("4")
$p4.StandardInput.Close()
$stdout4 = $p4.StandardOutput.ReadToEnd()
$timedOut4 = -not $p4.WaitForExit(45000)
if ($timedOut4) { $p4.Kill() }
Write-Host "=== MODE 4 ==="
$stdout4 | Select-Object -Last 20
if ($stdout4 -match 'Throughput:\s*(\d+)\s+FPS') { Write-Host "MODE4_FPS=$($Matches[1])" }
if ($stdout4 -match 'ms/frame.*?(\d+\.\d+)') { Write-Host "MODE4_ms_frame=$($Matches[1])" }
