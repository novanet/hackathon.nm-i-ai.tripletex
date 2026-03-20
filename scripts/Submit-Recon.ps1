<#
.SYNOPSIS
    Submit 3 dry-run recon submissions to capture competition prompts without consuming task attempts.
.DESCRIPTION
    Requires DRY_RUN=true on the running agent. Submits 3 times (max concurrency) with 5s gaps,
    then polls /tripletex/my/submissions every 10s until all 3 complete.
    Prompts are logged to logs/recon.jsonl by the agent.
.PARAMETER Token
    The access_token cookie value. Falls back to $env:AINM_TOKEN.
#>
param(
    [string]$Token
)

$ErrorActionPreference = "Stop"
$taskId = "cccccccc-cccc-cccc-cccc-cccccccccccc"
$apiBase = "https://api.ainm.no"

# --- Resolve auth token ---
if (-not $Token) { $Token = $env:AINM_TOKEN }
if (-not $Token) {
    Write-Host "No auth token. Set `$env:AINM_TOKEN or pass -Token." -ForegroundColor Red
    return
}

# --- Check agent is running ---
$agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if (-not $agent) {
    Write-Host "ERROR: TripletexAgent is not running. Start with DRY_RUN=true:" -ForegroundColor Red
    Write-Host '  $env:DRY_RUN = "true"; .\scripts\Start-Agent.ps1 -Background' -ForegroundColor Gray
    return
}
Write-Host "Agent running (PID $($agent.Id))" -ForegroundColor Green

# --- Get tunnel URL (try cloudflared first, then ngrok) ---
$tunnelUrl = $null

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
    Write-Host "ERROR: No tunnel found. Start one first." -ForegroundColor Red
    return
}

$endpointUrl = "$tunnelUrl/solve"
Write-Host "Endpoint: $endpointUrl" -ForegroundColor Cyan

# --- Submit 3 times with 5s gaps ---
$headers = @{
    "Cookie"       = "access_token=$Token"
    "Content-Type" = "application/json"
    "Origin"       = "https://app.ainm.no"
    "Referer"      = "https://app.ainm.no/"
}
$body = @{
    endpoint_url     = $endpointUrl
    endpoint_api_key = $null
} | ConvertTo-Json

$submissionIds = @()
$submitCount = 3

for ($i = 1; $i -le $submitCount; $i++) {
    Write-Host ""
    Write-Host "Submitting recon $i/$submitCount..." -ForegroundColor Cyan
    try {
        $result = Invoke-RestMethod -Uri "$apiBase/tasks/$taskId/submissions" `
            -Method POST -Headers $headers -Body $body -ErrorAction Stop
        $submissionIds += $result.id
        Write-Host "  ID: $($result.id) | Daily: $($result.daily_submissions_used)/$($result.daily_submissions_max)" -ForegroundColor Green
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 429) {
            Write-Host "  Rate limited (429) - max concurrent reached. Continuing with $($submissionIds.Count) submissions." -ForegroundColor Yellow
            break
        }
        else {
            Write-Host "  Failed ($statusCode): $_" -ForegroundColor Red
        }
    }

    if ($i -lt $submitCount) {
        Write-Host "  Waiting 5s..." -ForegroundColor Gray
        Start-Sleep -Seconds 5
    }
}

if ($submissionIds.Count -eq 0) {
    Write-Host "No submissions created." -ForegroundColor Red
    return
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " $($submissionIds.Count) recon submissions queued" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

# --- Poll until all complete ---
Write-Host ""
Write-Host "Polling for completion every 10s..." -ForegroundColor Gray
$maxPolls = 30  # 5 minutes max
$completedIds = @{}

for ($poll = 0; $poll -lt $maxPolls; $poll++) {
    Start-Sleep -Seconds 10

    try {
        $allSubs = Invoke-RestMethod -Uri "$apiBase/tripletex/my/submissions" `
            -Method GET -Headers @{ "Cookie" = "access_token=$Token" } -ErrorAction Stop
    }
    catch {
        Write-Host "  [$([datetime]::Now.ToString('HH:mm:ss'))] Poll error: $_" -ForegroundColor Yellow
        continue
    }

    $pending = 0
    foreach ($sid in $submissionIds) {
        if ($completedIds.ContainsKey($sid)) { continue }

        $sub = $allSubs | Where-Object { $_.id -eq $sid }
        if (-not $sub) {
            Write-Host "  [$([datetime]::Now.ToString('HH:mm:ss'))] $($sid.Substring(0,8))... not found yet" -ForegroundColor Gray
            $pending++
            continue
        }

        $state = $sub.status
        if ($state -eq "completed" -or $state -eq "failed" -or $state -eq "error") {
            $completedIds[$sid] = $true
            $score = if ($sub.score_raw -ne $null) { "$($sub.score_raw)/$($sub.score_max)" } else { "n/a" }
            $norm = if ($sub.normalized_score -ne $null) { $sub.normalized_score } else { "n/a" }
            $dur = if ($sub.duration_ms -ne $null) { "$([math]::Round($sub.duration_ms / 1000, 1))s" } else { "?" }
            $comment = if ($sub.feedback -and $sub.feedback.comment) { $sub.feedback.comment } else { "" }

            Write-Host ""
            Write-Host "  $($sid.Substring(0,8))... $state | Score: $score (norm: $norm) | Duration: $dur" -ForegroundColor $(if ($sub.score_raw -gt 0) { "Green" } else { "Yellow" })
            if ($comment) { Write-Host "    $comment" -ForegroundColor Gray }
            if ($sub.feedback -and $sub.feedback.checks) {
                foreach ($check in $sub.feedback.checks) {
                    $color = if ($check -match "passed") { "Green" } else { "Red" }
                    Write-Host "    $check" -ForegroundColor $color
                }
            }
        }
        else {
            $pending++
        }
    }

    $done = $completedIds.Count
    Write-Host "  [$([datetime]::Now.ToString('HH:mm:ss'))] $done/$($submissionIds.Count) completed" -NoNewline
    if ($pending -gt 0) { Write-Host " ($pending pending)" -ForegroundColor Gray } else { Write-Host "" }

    if ($done -eq $submissionIds.Count) { break }
}

# --- Summary ---
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Recon Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$reconFile = Join-Path $PSScriptRoot "..\src\logs\recon.jsonl"
if (Test-Path $reconFile) {
    $lines = Get-Content $reconFile
    Write-Host "  Prompts captured in recon.jsonl: $($lines.Count)" -ForegroundColor Green
    Write-Host ""
    foreach ($line in $lines) {
        try {
            $entry = $line | ConvertFrom-Json
            $promptPreview = if ($entry.prompt.Length -gt 80) { $entry.prompt.Substring(0, 80) + "..." } else { $entry.prompt }
            Write-Host "  [$($entry.task_type)] ($($entry.language)) $promptPreview" -ForegroundColor White
        }
        catch { }
    }
}
else {
    Write-Host "  No recon.jsonl found - agent may not be in DRY_RUN mode!" -ForegroundColor Red
}

Write-Host ""
