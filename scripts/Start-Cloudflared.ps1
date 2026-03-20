<#
.SYNOPSIS
    Starts a cloudflared tunnel to expose the agent via HTTPS.
.DESCRIPTION
    Uses Cloudflare's quick tunnel (no account needed) to expose localhost:5000.
    No interstitial page, no rate limits — more reliable than ngrok free tier.
.PARAMETER Port
    Local port to tunnel (default: 5000).
.PARAMETER Kill
    Stop any running cloudflared process and exit.
#>
param(
    [int]$Port = 5000,
    [switch]$Kill
)

# --- Kill mode ---
if ($Kill) {
    $procs = Get-Process -Name cloudflared -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        Write-Host "cloudflared stopped." -ForegroundColor Yellow
    }
    else {
        Write-Host "cloudflared is not running." -ForegroundColor Gray
    }
    return
}

# Ensure agent is running
$agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if (-not $agent) {
    Write-Host "WARNING: TripletexAgent is not running. Start it first with .\scripts\Start-Agent.ps1" -ForegroundColor Yellow
}

# Kill existing cloudflared if already running
$existing = Get-Process -Name cloudflared -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing cloudflared process..." -ForegroundColor Yellow
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# Start cloudflared and capture output to find the URL
Write-Host "Starting cloudflared tunnel to http://localhost:$Port ..." -ForegroundColor Cyan

$cloudflaredExe = Get-Command cloudflared -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $cloudflaredExe) {
    # Try winget package location
    $cloudflaredExe = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\*cloudflare*" -Recurse -Filter "cloudflared.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $cloudflaredExe) {
    Write-Host "ERROR: cloudflared not found. Install with: winget install Cloudflare.cloudflared" -ForegroundColor Red
    return
}

$logFile = Join-Path $PSScriptRoot "..\src\logs\cloudflared.log"
Start-Process -FilePath $cloudflaredExe `
    -ArgumentList "tunnel", "--url", "http://localhost:$Port", "--no-autoupdate" `
    -RedirectStandardError $logFile `
    -WindowStyle Hidden

# Wait for the URL to appear in the log
$maxWait = 20
$waited = 0
$tunnelUrl = $null
while ($waited -lt $maxWait) {
    Start-Sleep -Seconds 1
    $waited++
    if (Test-Path $logFile) {
        $content = Get-Content $logFile -Raw -ErrorAction SilentlyContinue
        if ($content -match '(https://[a-z0-9-]+\.trycloudflare\.com)') {
            $tunnelUrl = $Matches[1]
            break
        }
    }
}

if (-not $tunnelUrl) {
    Write-Host "ERROR: Could not get tunnel URL after ${maxWait}s." -ForegroundColor Red
    Write-Host "Check log: $logFile" -ForegroundColor Gray
    return
}

# Display submission info
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " cloudflared tunnel is running!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Endpoint URL:  " -NoNewline; Write-Host "$tunnelUrl/solve" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Submit at:     https://app.ainm.no/submit/tripletex" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Stop with:     .\scripts\Start-Cloudflared.ps1 -Kill" -ForegroundColor Gray
