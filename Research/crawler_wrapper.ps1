# Wrapper script for TD-Research-Crawler scheduled task
# Sources config_env.ps1 then runs the crawler (light mode by default)
$ErrorActionPreference = "Continue"
$envPath = Join-Path $PSScriptRoot "config_env.ps1"
if (Test-Path $envPath) {
    . $envPath
}
$scriptPath = Join-Path $PSScriptRoot "crawler.py"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = Join-Path $PSScriptRoot "logs\crawler_$timestamp.log"
if (-not (Test-Path (Join-Path $PSScriptRoot "logs"))) {
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot "logs") -Force | Out-Null
}
& python -u $scriptPath --light *>&1 | Out-File -FilePath $logFile -Encoding utf8