<#
.SYNOPSIS
    Starts a localtunnel to expose the agent via HTTPS.
.DESCRIPTION
    Runs localtunnel (via npx) to tunnel HTTP traffic from localhost:5000.
    Writes the tunnel URL to src/logs/localtunnel.log for Submit-Run.ps1 to detect.
.PARAMETER Port
    Local port to tunnel (default: 5000).
.PARAMETER Kill
    Stop any running localtunnel process and exit.
#>
param(
    [int]$Port = 5000,
    [switch]$Kill
)

# --- Kill mode ---
if ($Kill) {
    $procs = Get-Process -Name "lt", "node" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'localtunnel|/lt\b' -or $_.MainWindowTitle -match 'localtunnel' }
    # Broader: kill any node process whose command line references localtunnel
    $ltProcs = Get-Process -Name node -ErrorAction SilentlyContinue |
    Where-Object {
        try { (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -ErrorAction SilentlyContinue).CommandLine -match 'localtunnel|\\lt' }
        catch { $false }
    }
    if ($ltProcs) {
        $ltProcs | Stop-Process -Force
        Write-Host "localtunnel stopped." -ForegroundColor Yellow
    }
    else {
        Write-Host "localtunnel is not running (no matching node process found)." -ForegroundColor Gray
    }
    $logFile = Join-Path $PSScriptRoot "..\src\logs\localtunnel.log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force }
    return
}

# Ensure agent is running
$agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if (-not $agent) {
    Write-Host "WARNING: TripletexAgent is not running. Start it first with .\scripts\Start-Agent.ps1" -ForegroundColor Yellow
}

# Ensure logs directory exists
$logDir = Join-Path $PSScriptRoot "..\src\logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logFile = Join-Path $logDir "localtunnel.log"

# Clear old log
if (Test-Path $logFile) { Remove-Item $logFile -Force }

# Start localtunnel in background — redirect stdout to log file
Write-Host "Starting localtunnel to http://localhost:$Port ..." -ForegroundColor Cyan
$npxCmd = Get-Command npx.cmd -ErrorAction SilentlyContinue
if (-not $npxCmd) { $npxCmd = Get-Command npx -ErrorAction SilentlyContinue }
if (-not $npxCmd) {
    Write-Host "ERROR: npx not found. Install Node.js first." -ForegroundColor Red
    return
}
$proc = Start-Process -FilePath $npxCmd.Source -ArgumentList "--yes", "localtunnel", "--port", $Port `
    -RedirectStandardOutput $logFile -RedirectStandardError (Join-Path $logDir "localtunnel-err.log") `
    -WindowStyle Hidden -PassThru

Write-Host "localtunnel process started (PID $($proc.Id))" -ForegroundColor Green

# Wait for the URL to appear in the log
$maxWait = 15
$waited = 0
$tunnelUrl = $null
while ($waited -lt $maxWait) {
    Start-Sleep -Seconds 1
    $waited++
    if (Test-Path $logFile) {
        $content = Get-Content $logFile -Raw -ErrorAction SilentlyContinue
        if ($content -match '(https://[a-z0-9-]+\.loca\.lt)') {
            $tunnelUrl = $Matches[1]
            break
        }
    }
}

if (-not $tunnelUrl) {
    Write-Host "ERROR: Could not get tunnel URL after ${maxWait}s." -ForegroundColor Red
    Write-Host "Check log: $logFile" -ForegroundColor Gray
    if (Test-Path (Join-Path $logDir "localtunnel-err.log")) {
        Get-Content (Join-Path $logDir "localtunnel-err.log") | Write-Host -ForegroundColor Red
    }
    return
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " localtunnel ready!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "  URL: $tunnelUrl" -ForegroundColor Cyan
Write-Host ""
Write-Host "NOTE: localtunnel requires visitors to click through a splash page." -ForegroundColor Yellow
Write-Host "For API calls, the caller may need to set the 'Bypass-Tunnel-Reminder' header." -ForegroundColor Yellow
Write-Host ""
Write-Host "Use .\scripts\Start-Localtunnel.ps1 -Kill to stop" -ForegroundColor Gray
