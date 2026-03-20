<#
.SYNOPSIS
    Starts an ngrok tunnel to expose the agent via HTTPS.
.DESCRIPTION
    Runs ngrok in the background to tunnel HTTP traffic from localhost:5000.
    Queries the ngrok local API to display the public HTTPS URL and your API key,
    ready to paste into the submission page.
.PARAMETER Port
    Local port to tunnel (default: 5000).
.PARAMETER Kill
    Stop any running ngrok process and exit.
#>
param(
    [int]$Port = 5000,
    [switch]$Kill
)

# --- Kill mode ---
if ($Kill) {
    $procs = Get-Process -Name ngrok -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        Write-Host "ngrok stopped." -ForegroundColor Yellow
    }
    else {
        Write-Host "ngrok is not running." -ForegroundColor Gray
    }
    return
}

# Ensure agent is running
$agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if (-not $agent) {
    Write-Host "WARNING: TripletexAgent is not running. Start it first with .\scripts\Start-Agent.ps1" -ForegroundColor Yellow
}

# Kill existing ngrok if already running
$existing = Get-Process -Name ngrok -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing ngrok process..." -ForegroundColor Yellow
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# Start ngrok in background
Write-Host "Starting ngrok tunnel to http://localhost:$Port ..." -ForegroundColor Cyan
Start-Process -FilePath "ngrok" -ArgumentList "http", "http://localhost:$Port" -WindowStyle Hidden

# Wait for ngrok API to become available
$maxWait = 10
$waited = 0
$tunnelUrl = $null
while ($waited -lt $maxWait) {
    Start-Sleep -Seconds 1
    $waited++
    try {
        $response = Invoke-RestMethod -Uri "http://localhost:4040/api/tunnels" -ErrorAction Stop
        $tunnel = $response.tunnels | Where-Object { $_.proto -eq "https" } | Select-Object -First 1
        if ($tunnel) {
            $tunnelUrl = $tunnel.public_url
            break
        }
    }
    catch {
        # ngrok not ready yet
    }
}

if (-not $tunnelUrl) {
    Write-Host "ERROR: Could not get tunnel URL from ngrok API after ${maxWait}s." -ForegroundColor Red
    Write-Host "Check if ngrok is running: Get-Process ngrok" -ForegroundColor Gray
    return
}

# Read API key from user-secrets
$apiKey = $null
$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
if ($secretsJson) {
    foreach ($line in $secretsJson) {
        if ($line -match '^ApiKey\s*=\s*(.+)$') {
            $apiKey = $Matches[1].Trim()
        }
    }
}

# Display submission info
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " ngrok tunnel is running!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Endpoint URL:  " -NoNewline; Write-Host "$tunnelUrl/solve" -ForegroundColor Yellow
if ($apiKey) {
    Write-Host "  API Key:       " -NoNewline; Write-Host "$apiKey" -ForegroundColor Yellow
}
else {
    Write-Host "  API Key:       " -NoNewline; Write-Host "(not found in user-secrets)" -ForegroundColor Red
}
Write-Host ""
Write-Host "  Submit at:     https://app.ainm.no/submit/tripletex" -ForegroundColor Cyan
Write-Host ""
Write-Host "  To stop:       .\scripts\Start-Tunnel.ps1 -Kill" -ForegroundColor Gray
Write-Host "  ngrok inspect: http://localhost:4040" -ForegroundColor Gray
Write-Host ""
