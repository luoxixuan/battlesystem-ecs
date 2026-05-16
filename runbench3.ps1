$ErrorActionPreference = "Continue"
$proj = "F:\AI\BattleSystem-ECS"
$tmp = "$env:TEMP"
$mode2 = "2"
$mode4 = "4"

# Create temp input files
Set-Content -Path "$tmp\bench_in2.txt" -Value $mode2 -NoNewline
Set-Content -Path "$tmp\bench_in4.txt" -Value $mode4 -NoNewline

# Mode 2: use file redirection via cmd
$psi2 = "cmd /c echo 2 | dotnet run -c Release > `"$tmp\bench2_out.txt`" 2>&1"
$job2 = Start-Process powershell -ArgumentList "-NoExit", "-Command", $psi2 -PassThru -WindowStyle Hidden
$null = $job2 | Wait-Process -Timeout 45
$out2 = Get-Content "$tmp\bench2_out.txt" -Raw -ErrorAction SilentlyContinue
Write-Host "=== MODE 2 ==="
if ($out2 -match 'Throughput:\s*(\d+)\s+FPS') {
    Write-Host "FPS=$($Matches[1])"
} else {
    Write-Host "NO BENCHMARK OUTPUT FOUND"
    Write-Host ($out2 -replace '(?s)^.*?(?=Throughput|BENCHMARK|选择)' -replace '(?s).*(选择|BENCHMARK|Throughput).*$')
}

# Mode 4: use file redirection via cmd
$psi4 = "cmd /c echo 4 | dotnet run -c Release > `"$tmp\bench4_out.txt`" 2>&1"
$job4 = Start-Process powershell -ArgumentList "-NoExit", "-Command", $psi4 -PassThru -WindowStyle Hidden
$null = $job4 | Wait-Process -Timeout 45
$out4 = Get-Content "$tmp\bench4_out.txt" -Raw -ErrorAction SilentlyContinue
Write-Host "=== MODE 4 ==="
if ($out4 -match 'Throughput:\s*(\d+)\s+FPS') {
    Write-Host "FPS=$($Matches[1])"
} else {
    Write-Host "NO BENCHMARK OUTPUT FOUND"
}
