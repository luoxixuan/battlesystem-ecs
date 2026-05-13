# Schedule-TDCrawler.ps1
# Creates a Windows scheduled task to run the TD research crawler every 2 hours.
# Run this ONCE with admin privileges.

$ErrorActionPreference = "Stop"

# Load local secrets (not committed to git)
$configEnv = Join-Path $PSScriptRoot "config_env.ps1"
if (Test-Path $configEnv) {
    Write-Host "[*] Loading environment config..."
    . $configEnv
}

$taskName = "TD-Research-Crawler"
$scriptPath = "F:\AI\BattleSystem-ECS\Research\crawler.py"
$pythonExe = (Get-Command python).Source
$workingDir = "F:\AI\BattleSystem-ECS\Research"
$logDir = "F:\AI\BattleSystem-ECS\Research\logs"

# Create log directory
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

Write-Host "=== TD Research Crawler Scheduler ==="
Write-Host "Python: $pythonExe"
Write-Host "Script: $scriptPath"
Write-Host "Logs:   $logDir"
if ($env:GITHUB_TOKEN) { Write-Host "GitHub Token: SET" } else { Write-Host "GitHub Token: NOT SET" }
Write-Host ""

# Remove existing task if present
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[!] Removing existing task '$taskName'..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

# Build environment variables for scheduled task (including GitHub token if set)
$taskEnv = @{}
if ($env:GITHUB_TOKEN) { $taskEnv["GITHUB_TOKEN"] = $env:GITHUB_TOKEN }

# Create the scheduled task action
$actionParams = @{
    Execute = $pythonExe
    Argument = "-u `"$scriptPath`" --light"
    WorkingDirectory = $workingDir
}
if ($taskEnv.Count -gt 0) { $actionParams["Environment"] = $taskEnv }
$action = New-ScheduledTaskAction @actionParams

# Trigger: every 2 hours, starting now
$trigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Hours 2) `
    -RepetitionDuration (New-TimeSpan -Days 365)

# Settings: don't run if missed, stop if runs too long
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 15)

# Register task
Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description "Crawls GitHub for high-star tower defense projects every 2 hours" `
    -RunLevel Limited `
    -Force

Write-Host "[+] Task '$taskName' created successfully!"
Write-Host "[+] Runs every 2 hours (light mode: search only)."
Write-Host ""

# Also create a daily deep-dive task
$deepTaskName = "TD-Research-DeepDive"
$deepExisting = Get-ScheduledTask -TaskName $deepTaskName -ErrorAction SilentlyContinue
if ($deepExisting) {
    Unregister-ScheduledTask -TaskName $deepTaskName -Confirm:$false
}

$deepActionParams = @{
    Execute = $pythonExe
    Argument = "-u `"$scriptPath`""
    WorkingDirectory = $workingDir
}
if ($taskEnv.Count -gt 0) { $deepActionParams["Environment"] = $taskEnv }
$deepAction = New-ScheduledTaskAction @deepActionParams

$deepTrigger = New-ScheduledTaskTrigger -Daily -At "02:00"

$deepSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30)

Register-ScheduledTask `
    -TaskName $deepTaskName `
    -Action $deepAction `
    -Trigger $deepTrigger `
    -Settings $deepSettings `
    -Description "Daily deep-dive crawl: fetches README + file tree for architecture analysis" `
    -RunLevel Limited `
    -Force

Write-Host "[+] Deep-dive task '$deepTaskName' created (daily at 2:00 AM)."
Write-Host ""
Write-Host "Manual commands:"
Write-Host "  Start:  schtasks /run /tn '$taskName'"
Write-Host "  Status: schtasks /query /tn '$taskName'"
Write-Host "  Logs:   dir $logDir"
Write-Host "  Remove: schtasks /delete /tn '$taskName' /f"
