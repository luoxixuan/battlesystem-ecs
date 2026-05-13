# Schedule-TDCrawler.ps1
# Creates a Windows scheduled task to run the TD research crawler every 2 hours.
# Run this ONCE with admin privileges.

$ErrorActionPreference = "Stop"

$taskName = "TD-Research-Crawler"
$wrapperScript = "F:\AI\BattleSystem-ECS\Research\crawler_wrapper.ps1"
$workingDir = "F:\AI\BattleSystem-ECS\Research"
$logDir = "F:\AI\BattleSystem-ECS\Research\logs"

# Load environment before registering tasks
$configEnv = Join-Path $PSScriptRoot "config_env.ps1"
if (Test-Path $configEnv) {
    Write-Host "[*] Loading environment config..."
    . $configEnv
    if ($env:GITHUB_TOKEN) { Write-Host "[+] GITHUB_TOKEN loaded" }
} else {
    Write-Host "[!] config_env.ps1 not found — token will not be available"
}

# Create log directory
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

Write-Host ""
Write-Host "=== TD Research Crawler Scheduler ==="
Write-Host "Wrapper:  $wrapperScript"
Write-Host "Logs:     $logDir"
Write-Host ""

# Remove existing task if present
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[!] Removing existing task '$taskName'..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

# Create the scheduled task action (use wrapper so config_env.ps1 is sourced)
$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-ExecutionPolicy Bypass -File `"$wrapperScript`"" `
    -WorkingDirectory $workingDir

# Trigger: every 2 hours, starting 1 minute from now
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

Write-Host "[+] Task '$taskName' registered (every 2 hours)"
Write-Host ""

# Also create a daily deep-dive task
$deepTaskName = "TD-Research-DeepDive"
$deepWrapper = "F:\AI\BattleSystem-ECS\Research\crawler_deep_wrapper.ps1"
$deepExisting = Get-ScheduledTask -TaskName $deepTaskName -ErrorAction SilentlyContinue
if ($deepExisting) {
    Unregister-ScheduledTask -TaskName $deepTaskName -Confirm:$false
}

$deepAction = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-ExecutionPolicy Bypass -File `"$deepWrapper`"" `
    -WorkingDirectory $workingDir

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

Write-Host "[+] Task '$deepTaskName' registered (daily at 02:00 AM)"
Write-Host ""
Write-Host "Manual commands:"
Write-Host "  Start:  schtasks /run /tn '$taskName'"
Write-Host "  Status: schtasks /query /tn '$taskName'"
Write-Host "  Logs:   dir $logDir"
Write-Host "  Remove: schtasks /delete /tn '$taskName' /f"