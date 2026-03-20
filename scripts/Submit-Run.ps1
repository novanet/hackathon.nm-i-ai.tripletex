<#
.SYNOPSIS
    Submit a competition run to the NM i AI platform.
.DESCRIPTION
    Ensures the agent and ngrok tunnel are running, then submits the endpoint URL
    to the competition API. Optionally polls for completion status.
.PARAMETER Token
    The access_token cookie value for authentication. If not provided, reads from
    environment variable AINM_TOKEN or prompts.
.PARAMETER NoWait
    Don't poll for submission status after submitting.
#>
param(
    [string]$Token,
    [switch]$NoWait
)

$ErrorActionPreference = "Stop"
$taskId = "cccccccc-cccc-cccc-cccc-cccccccccccc"
$apiBase = "https://api.ainm.no"

# --- Resolve auth token ---
if (-not $Token) {
    $Token = $env:AINM_TOKEN
}
if (-not $Token) {
    Write-Host "No auth token provided. Set `$env:AINM_TOKEN or pass -Token." -ForegroundColor Red
    Write-Host "Get it from browser DevTools > Application > Cookies > access_token" -ForegroundColor Gray
    return
}

# --- Check agent is running ---
$agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if (-not $agent) {
    Write-Host "ERROR: TripletexAgent is not running. Start it first:" -ForegroundColor Red
    Write-Host "  .\scripts\Start-Agent.ps1 -Background" -ForegroundColor Gray
    return
}
Write-Host "Agent running (PID $($agent.Id))" -ForegroundColor Green

# --- Get tunnel URL (try cloudflared first, then ngrok) ---
$tunnelUrl = $null

# Try cloudflared log
$cfLog = Join-Path $PSScriptRoot "..\src\logs\cloudflared.log"
if (Test-Path $cfLog) {
    $cfContent = Get-Content $cfLog -Raw -ErrorAction SilentlyContinue
    if ($cfContent -match '(https://[a-z0-9-]+\.trycloudflare\.com)') {
        $cfProc = Get-Process -Name cloudflared -ErrorAction SilentlyContinue
        if ($cfProc) {
            $tunnelUrl = $Matches[1]
            Write-Host "Using cloudflared tunnel" -ForegroundColor Green
        }
    }
}

# Try localtunnel log
if (-not $tunnelUrl) {
    $ltLog = Join-Path $PSScriptRoot "..\src\logs\localtunnel.log"
    if (Test-Path $ltLog) {
        $ltContent = Get-Content $ltLog -Raw -ErrorAction SilentlyContinue
        if ($ltContent -match '(https://[a-z0-9-]+\.loca\.lt)') {
            $tunnelUrl = $Matches[1]
            Write-Host "Using localtunnel" -ForegroundColor Green
        }
    }
}

# Fallback to ngrok
if (-not $tunnelUrl) {
    try {
        $response = Invoke-RestMethod -Uri "http://localhost:4040/api/tunnels" -ErrorAction Stop
        $tunnel = $response.tunnels | Where-Object { $_.proto -eq "https" } | Select-Object -First 1
        if ($tunnel) {
            $tunnelUrl = $tunnel.public_url
            Write-Host "Using ngrok tunnel" -ForegroundColor Green
        }
    }
    catch { }
}

if (-not $tunnelUrl) {
    Write-Host "ERROR: No tunnel found. Start one first:" -ForegroundColor Red
    Write-Host "  .\scripts\Start-Localtunnel.ps1  (localtunnel)" -ForegroundColor Gray
    Write-Host "  .\scripts\Start-Tunnel.ps1       (ngrok)" -ForegroundColor Gray
    Write-Host "  .\scripts\Start-Cloudflared.ps1  (cloudflared)" -ForegroundColor Gray
    return
}

$endpointUrl = "$tunnelUrl/solve"
Write-Host "Endpoint: $endpointUrl" -ForegroundColor Cyan

# --- Quick health check ---
try {
    $health = Invoke-WebRequest -Uri "$tunnelUrl/solve" -Method POST -ContentType "application/json" `
        -Body '{"prompt":"ping","files":[],"tripletex_credentials":{"base_url":"https://test","session_token":"test"}}' `
        -ErrorAction Stop -TimeoutSec 30
    Write-Host "Health check: $($health.StatusCode)" -ForegroundColor Green
}
catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -and $code -eq 200) {
        Write-Host "Health check: OK" -ForegroundColor Green
    }
    else {
        Write-Host "WARNING: Health check failed ($code). Submitting anyway..." -ForegroundColor Yellow
    }
}

# --- Submit ---
Write-Host ""
Write-Host "Submitting to competition..." -ForegroundColor Cyan

$body = @{
    endpoint_url     = $endpointUrl
    endpoint_api_key = $null
} | ConvertTo-Json

$headers = @{
    "Cookie"       = "access_token=$Token"
    "Content-Type" = "application/json"
    "Origin"       = "https://app.ainm.no"
    "Referer"      = "https://app.ainm.no/"
}

try {
    $result = Invoke-RestMethod -Uri "$apiBase/tasks/$taskId/submissions" `
        -Method POST -Headers $headers -Body $body -ErrorAction Stop
}
catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    if ($statusCode -eq 429) {
        Write-Host "ERROR: Max 3 in-flight submissions. Wait for current runs to complete." -ForegroundColor Red
    }
    else {
        Write-Host "ERROR: Submission failed ($statusCode): $_" -ForegroundColor Red
    }
    return
}

$submissionId = $result.id
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " Submission queued!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "  ID:     $submissionId"
Write-Host "  Status: $($result.status)"
Write-Host "  Daily:  $($result.daily_submissions_used) / $($result.daily_submissions_max)"
Write-Host ""

if ($NoWait) { return }

# --- Poll for completion ---
Write-Host "Polling for results (Ctrl+C to stop)..." -ForegroundColor Gray
$pollInterval = 10
$maxPolls = 60  # 10 minutes max

for ($i = 0; $i -lt $maxPolls; $i++) {
    Start-Sleep -Seconds $pollInterval
    try {
        # Use list endpoint and find our submission (individual endpoint returns 404)
        $allSubs = Invoke-RestMethod -Uri "$apiBase/tripletex/my/submissions" `
            -Method GET -Headers @{ "Cookie" = "access_token=$Token" } -ErrorAction Stop

        $mySub = $allSubs | Where-Object { $_.id -eq $submissionId } | Select-Object -First 1
        if (-not $mySub) {
            Write-Host "  [$([datetime]::Now.ToString('HH:mm:ss'))] Submission not in list yet..." -ForegroundColor Gray
            continue
        }

        $state = $mySub.status
        Write-Host "  [$([datetime]::Now.ToString('HH:mm:ss'))] Status: $state" -NoNewline

        if ($mySub.PSObject.Properties["score"] -and $null -ne $mySub.score) {
            Write-Host " | Score: $($mySub.score)" -ForegroundColor Yellow
        }
        else {
            Write-Host ""
        }

        if ($state -eq "completed" -or $state -eq "failed" -or $state -eq "error") {
            Write-Host ""
            Write-Host "Final result:" -ForegroundColor Cyan
            $mySub | ConvertTo-Json -Depth 5 | Write-Host
            break
        }
    }
    catch {
        Write-Host "  [$([datetime]::Now.ToString('HH:mm:ss'))] Poll error: $_" -ForegroundColor Yellow
    }
}
