# Wrapper script for TD-Research-Crawler scheduled task
# Sources config_env.ps1 then runs the crawler
$ErrorActionPreference = "Continue"
$envPath = Join-Path $PSScriptRoot "config_env.ps1"
if (Test-Path $envPath) {
    . $envPath
}
$scriptPath = Join-Path $PSScriptRoot "crawler.py"
& python -u $scriptPath --light