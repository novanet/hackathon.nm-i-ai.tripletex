<#
.SYNOPSIS
    Submit a competition run to the NM i AI platform.
.DESCRIPTION
    Ensures the agent and tunnel are running (auto-starts if needed), submits the
    endpoint URL to the competition API, polls for completion (2 min), then replays
    new competition requests locally via Test-Solve.ps1 for analysis.
.PARAMETER Token
    The access_token cookie value for authentication. If not provided, reads from
    environment variable AINM_TOKEN or prompts.
.PARAMETER NoWait
    Don't poll for submission status after submitting.
.PARAMETER NoReplay
    Don't replay competition requests locally after completion.
#>
param(
    [string]$Token,
    [switch]$NoWait,
    [switch]$NoReplay
)

$ErrorActionPreference = "Stop"
$taskId = "cccccccc-cccc-cccc-cccc-cccccccccccc"
$apiBase = "https://api.ainm.no"
$leaderboardUrl = "$apiBase/tripletex/leaderboard/996fca4f-53fc-4585-bc65-b7a632fe7478"

# --- Resolve auth token ---
if (-not $Token) {
    $Token = $env:AINM_TOKEN
}
if (-not $Token) {
    # Try user-secrets
    $secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
    if ($secretsJson) {
        foreach ($line in $secretsJson) {
            if ($line -match '^AinmToken\s*=\s*(.+)$') {
                $Token = $Matches[1].Trim()
                break
            }
        }
    }
}
if (-not $Token) {
    Write-Host "No auth token provided. Set `$env:AINM_TOKEN, pass -Token, or configure AinmToken in user-secrets." -ForegroundColor Red
    Write-Host "  dotnet user-secrets set AinmToken '<value>' --project src --id 54b40cce-1f78-4e18-ab1b-c1501ef7f7da" -ForegroundColor Gray
    return
}

# --- Check agent is running (auto-start if not) ---
$agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if (-not $agent) {
    Write-Host "TripletexAgent not running — starting..." -ForegroundColor Yellow
    & "$PSScriptRoot\Start-Agent.ps1" -Background
    Start-Sleep -Seconds 2
    $agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
    if (-not $agent) {
        Write-Host "ERROR: Failed to start TripletexAgent." -ForegroundColor Red
        return
    }
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
    Write-Host "No tunnel found — starting cloudflared..." -ForegroundColor Yellow
    & "$PSScriptRoot\Start-Cloudflared.ps1"
    # Re-read cloudflared log for the URL
    Start-Sleep -Seconds 2
    if (Test-Path $cfLog) {
        $cfContent = Get-Content $cfLog -Raw -ErrorAction SilentlyContinue
        if ($cfContent -match '(https://[a-z0-9-]+\.trycloudflare\.com)') {
            $tunnelUrl = $Matches[1]
            Write-Host "Using cloudflared tunnel" -ForegroundColor Green
        }
    }
}

if (-not $tunnelUrl) {
    Write-Host "ERROR: Could not start tunnel. Try manually:" -ForegroundColor Red
    Write-Host "  .\scripts\Start-Cloudflared.ps1" -ForegroundColor Gray
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

# --- Snapshot leaderboard counts for diff after submission ---
$leaderboardFile = Join-Path $PSScriptRoot "..\src\logs\leaderboard.jsonl"
$preCounts = @{}
$preScores = @{}
if (Test-Path $leaderboardFile) {
    $lastLine = (Get-Content $leaderboardFile -Encoding UTF8 | Select-Object -Last 1)
    if ($lastLine) {
        $prevEntry = $lastLine | ConvertFrom-Json
        foreach ($t in $prevEntry.tasks) {
            $preCounts[$t.tx_task_id] = $t.total_attempts
            $preScores[$t.tx_task_id] = $t.best_score
        }
    }
}

# --- Snapshot submissions.jsonl line count for replay ---
$submissionsFile = Join-Path $PSScriptRoot "..\src\logs\submissions.jsonl"
$preLineCount = 0
if (Test-Path $submissionsFile) {
    $preLineCount = (Get-Content $submissionsFile).Count
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

# --- Poll for completion (2 minutes) ---
Write-Host "Polling for results (2 min, Ctrl+C to stop)..." -ForegroundColor Gray
$pollInterval = 10
$maxPolls = 12  # 2 minutes

$finalState = $null
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
            $finalState = $state
            break
        }
    }
    catch {
        Write-Host "  [$([datetime]::Now.ToString('HH:mm:ss'))] Poll error: $_" -ForegroundColor Yellow
    }
}

if (-not $finalState) {
    Write-Host ""
    Write-Host "Polling timed out after 2 minutes. Check status manually." -ForegroundColor Yellow
}

# --- Persist results to results.jsonl ---
if ($finalState -and $mySub) {
    try {
        $resultsFile = Join-Path $PSScriptRoot "..\src\logs\results.jsonl"

        # Parse checks
        $checks = @()
        $passedCount = 0
        $failedCount = 0
        if ($mySub.feedback -and $mySub.feedback.checks) {
            foreach ($chk in $mySub.feedback.checks) {
                $passed = $chk -match ': passed'
                $checks += @{ text = $chk; passed = $passed }
                if ($passed) { $passedCount++ } else { $failedCount++ }
            }
        }

        # Read task summaries from new submissions.jsonl entries
        $taskSummaries = @()
        if (Test-Path $submissionsFile) {
            $currentLines = Get-Content $submissionsFile
            $newTaskLines = $currentLines | Select-Object -Skip $preLineCount
            foreach ($tl in $newTaskLines) {
                try {
                    $te = $tl | ConvertFrom-Json
                    if ($te.prompt -eq "ping") { continue }
                    $taskSummaries += @{
                        task_type   = $te.task_type
                        handler     = $te.handler
                        success     = $te.success
                        error       = $te.error
                        call_count  = $te.call_count
                        error_count = $te.error_count
                        elapsed_ms  = $te.elapsed_ms
                    }
                }
                catch { }
            }
        }

        $resultEntry = @{
            submission_id    = $submissionId
            timestamp        = (Get-Date).ToUniversalTime().ToString("o")
            status           = $finalState
            score_raw        = $mySub.score_raw
            score_max        = $mySub.score_max
            normalized_score = $mySub.normalized_score
            duration_ms      = $mySub.duration_ms
            feedback_comment = if ($mySub.feedback) { $mySub.feedback.comment } else { $null }
            total_checks     = $passedCount + $failedCount
            passed_checks    = $passedCount
            failed_checks    = $failedCount
            checks           = $checks
            task_count       = $taskSummaries.Count
            tasks            = $taskSummaries
        }

        $json = $resultEntry | ConvertTo-Json -Depth 5 -Compress
        [System.IO.Directory]::CreateDirectory((Split-Path $resultsFile)) | Out-Null
        Add-Content -Path $resultsFile -Value $json -Encoding UTF8

        Write-Host ""
        Write-Host "Results saved to results.jsonl" -ForegroundColor Green
        Write-Host "  Score: $($mySub.score_raw)/$($mySub.score_max) | Checks: $passedCount/$($passedCount + $failedCount) passed | Tasks: $($taskSummaries.Count)" -ForegroundColor Cyan
    }
    catch {
        Write-Host "WARNING: Failed to save results: $_" -ForegroundColor Yellow
    }
}

# --- Fetch leaderboard snapshot ---
try {
    $leaderboardFile = Join-Path $PSScriptRoot "..\src\logs\leaderboard.jsonl"
    [System.IO.Directory]::CreateDirectory((Split-Path $leaderboardFile)) | Out-Null

    $leaderboardData = Invoke-RestMethod -Uri $leaderboardUrl -Method GET -TimeoutSec 15 -ErrorAction Stop

    $totalScore = ($leaderboardData | Measure-Object -Property best_score -Sum).Sum
    $taskCount = ($leaderboardData | Measure-Object).Count
    $zeroTasks = $leaderboardData | Where-Object { $_.best_score -eq 0 } | ForEach-Object { $_.tx_task_id }

    # Diff attempt counts and scores vs pre-submission snapshot
    $attemptedTasks = @()
    $scoreChanges = @()
    foreach ($t in $leaderboardData) {
        $tid = $t.tx_task_id
        $prevAttempts = if ($preCounts.ContainsKey($tid)) { $preCounts[$tid] } else { 0 }
        $prevScore = if ($preScores.ContainsKey($tid)) { $preScores[$tid] } else { 0 }
        if ($t.total_attempts -gt $prevAttempts) {
            $attemptedTasks += @{
                tx_task_id    = $tid
                prev_attempts = $prevAttempts
                new_attempts  = $t.total_attempts
                delta         = $t.total_attempts - $prevAttempts
                prev_score    = $prevScore
                new_score     = $t.best_score
            }
        }
        if ($t.best_score -ne $prevScore) {
            $scoreChanges += @{
                tx_task_id = $tid
                prev_score = $prevScore
                new_score  = $t.best_score
                delta      = [Math]::Round($t.best_score - $prevScore, 4)
            }
        }
    }

    $leaderboardEntry = @{
        timestamp        = (Get-Date).ToUniversalTime().ToString("o")
        submission_id    = $submissionId
        total_best_score = [Math]::Round($totalScore, 4)
        task_count       = $taskCount
        attempted_tasks  = $attemptedTasks
        score_changes    = $scoreChanges
        tasks            = $leaderboardData
    }

    $lbJson = $leaderboardEntry | ConvertTo-Json -Depth 5 -Compress
    Add-Content -Path $leaderboardFile -Value $lbJson -Encoding UTF8

    Write-Host ""
    Write-Host "Leaderboard snapshot saved to leaderboard.jsonl" -ForegroundColor Green
    Write-Host "  Total: $([Math]::Round($totalScore, 2)) across $taskCount tasks" -ForegroundColor Yellow
    if ($zeroTasks) {
        Write-Host "  Zero-score tasks: $($zeroTasks -join ', ')" -ForegroundColor Yellow
    }

    # --- Known check names per task_type (best-effort annotation for anonymous "Check N") ---
    $knownChecks = @{
        "create_employee"       = @("employee_found", "firstName", "lastName", "email", "admin_role")
        "create_customer"       = @("customer_found", "name", "email", "organizationNumber", "addr.addressLine1", "addr.postalCode", "addr.city", "phoneNumber")
        "create_supplier"       = @("supplier_found", "name", "email", "organizationNumber", "phoneNumber")
        "create_product"        = @("product_found", "name", "number", "price")
        "create_department"     = @("department_found", "name", "departmentNumber")
        "create_project"        = @("project_found", "name", "has_customer", "has_project_manager")
        "create_invoice"        = @("invoice_found", "has_customer", "has_amount", "correct_amount")
        "register_payment"      = @("invoice_found", "payment_registered")
        "run_payroll"           = @("salary_transaction_found", "has_employee_link", "payslip_generated", "correct_amount")
        "create_travel_expense" = @("travel_expense_found", "has_title", "has_employee", "has_costs")
        "create_credit_note"    = @("credit_note_created")
        "create_voucher"        = @("voucher_found", "has_description", "has_postings")
    }

    # --- Load / update persistent tx_task_id → task_type mapping ---
    $mappingFile = Join-Path $PSScriptRoot "..\src\logs\task_mapping.json"
    $taskMapping = @{}
    if (Test-Path $mappingFile) {
        try {
            $taskMapping = Get-Content $mappingFile -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
        }
        catch { $taskMapping = @{} }
    }

    # --- Correlate: sort attempted tasks and new submissions by timestamp, zip positionally ---
    $correlated = @()
    $sortedAttempted = @()
    if ($attemptedTasks.Count -gt 0) {
        # Sort leaderboard attempted tasks by last_attempt_at ascending
        $sortedAttempted = $attemptedTasks | ForEach-Object {
            $tid = $_.tx_task_id
            $lb = $leaderboardData | Where-Object { $_.tx_task_id -eq $tid } | Select-Object -First 1
            $_ | Add-Member -NotePropertyName last_attempt_at -NotePropertyValue $lb.last_attempt_at -PassThru
        } | Sort-Object { [DateTimeOffset]::Parse($_.last_attempt_at) }

        # Sort new submissions.jsonl entries (non-ping) by timestamp ascending
        $sortedSubmissions = @()
        if (Test-Path $submissionsFile) {
            $currentLines = Get-Content $submissionsFile -Encoding UTF8
            $newSubLines = $currentLines | Select-Object -Skip $preLineCount
            foreach ($sl in $newSubLines) {
                try {
                    $se = $sl | ConvertFrom-Json
                    if ($se.prompt -eq "ping" -or $se.task_type -eq "unknown") { continue }
                    $sortedSubmissions += $se
                }
                catch { }
            }
            $sortedSubmissions = $sortedSubmissions | Sort-Object { [DateTimeOffset]::Parse($_.timestamp) }
        }

        # Zip positionally
        $len = [Math]::Min($sortedAttempted.Count, $sortedSubmissions.Count)
        for ($i = 0; $i -lt $len; $i++) {
            $at = $sortedAttempted[$i]
            $sub = $sortedSubmissions[$i]
            $correlated += @{
                tx_task_id  = $at.tx_task_id
                task_type   = $sub.task_type
                handler     = $sub.handler
                success     = $sub.success
                error       = $sub.error
                call_count  = $sub.call_count
                error_count = $sub.error_count
                prev_score  = $at.prev_score
                new_score   = $at.new_score
                timestamp   = $sub.timestamp
            }
            # Update persistent mapping (append-only — never overwrite existing)
            if (-not $taskMapping.ContainsKey($at.tx_task_id)) {
                $taskMapping[$at.tx_task_id] = $sub.task_type
            }
        }
    }

    # Save updated mapping
    try {
        [System.IO.Directory]::CreateDirectory((Split-Path $mappingFile)) | Out-Null
        $taskMapping | ConvertTo-Json -Compress | Set-Content -Path $mappingFile -Encoding UTF8
    }
    catch {
        Write-Host "WARNING: Failed to save task_mapping.json: $_" -ForegroundColor Yellow
    }

    # --- Print correlated task summary ---
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " Task Correlation (tx_id → handler → score)" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    if ($correlated.Count -gt 0) {
        foreach ($c in $correlated) {
            $scoreDelta = [Math]::Round($c.new_score - $c.prev_score, 4)
            $deltaStr = if ($scoreDelta -gt 0) { "+$scoreDelta * IMPROVED" } elseif ($scoreDelta -lt 0) { "$scoreDelta REGRESSED" } else { "= no change" }
            $statusStr = if ($c.success) { "OK" } else { "FAIL" }
            $errStr = if ($c.error) { "  err: $($c.error)" } else { "" }
            $color = if ($scoreDelta -gt 0) { "Green" } elseif ($scoreDelta -lt 0) { "Red" } elseif (-not $c.success) { "Yellow" } else { "Gray" }
            Write-Host ("  tx:{0} = {1,-26} | {2,-22} | {3,4} | {4} → {5} ({6}){7}" -f `
                    $c.tx_task_id, $c.task_type, $c.handler, $statusStr, `
                    $c.prev_score, $c.new_score, $deltaStr, $errStr) -ForegroundColor $color
        }
    }
    else {
        Write-Host "  No correlations (no new task attempts or no new submissions)." -ForegroundColor Gray
    }

    # Tasks attempted but not correlated (extra leaderboard attempts beyond what we logged)
    if ($sortedAttempted -and $sortedAttempted.Count -gt $correlated.Count) {
        Write-Host ""
        Write-Host "  Uncorrelated attempts (leaderboard only):" -ForegroundColor Gray
        for ($i = $correlated.Count; $i -lt $sortedAttempted.Count; $i++) {
            $at = $sortedAttempted[$i]
            $knownType = if ($taskMapping.ContainsKey($at.tx_task_id)) { $taskMapping[$at.tx_task_id] } else { "?" }
            Write-Host ("    tx:{0} = {1,-26} | score: {2} → {3}" -f $at.tx_task_id, $knownType, $at.prev_score, $at.new_score) -ForegroundColor Gray
        }
    }

    # --- Print annotated competition checks per correlated task ---
    if ($correlated.Count -gt 0 -and $finalState -and $mySub -and $mySub.feedback -and $mySub.feedback.checks) {
        $allChecks = @($mySub.feedback.checks)
        $checkOffset = 0

        Write-Host ""
        Write-Host "Competition checks (annotated):" -ForegroundColor Cyan

        foreach ($c in $correlated) {
            $checkNames = if ($knownChecks.ContainsKey($c.task_type)) { $knownChecks[$c.task_type] } else { @() }
            $nChecks = $checkNames.Count

            if ($nChecks -eq 0 -or $checkOffset -ge $allChecks.Count) {
                Write-Host "  tx:$($c.tx_task_id) $($c.task_type): (no check mapping)" -ForegroundColor Gray
                continue
            }

            # Grab the next $nChecks from the competition check list
            $taskChecks = $allChecks[$checkOffset .. ([Math]::Min($checkOffset + $nChecks - 1, $allChecks.Count - 1))]
            $checkOffset += $nChecks

            $passedN = ($taskChecks | Where-Object { $_ -match ': passed' }).Count
            $totalN = $taskChecks.Count
            $headerColor = if ($passedN -eq $totalN) { "Green" } elseif ($passedN -eq 0) { "Red" } else { "Yellow" }
            Write-Host ("  tx:{0} {1}: {2}/{3} passed" -f $c.tx_task_id, $c.task_type, $passedN, $totalN) -ForegroundColor $headerColor

            for ($ci = 0; $ci -lt $taskChecks.Count; $ci++) {
                $chk = $taskChecks[$ci]
                $name = if ($ci -lt $checkNames.Count) { $checkNames[$ci] } else { "check_$($ci+1)" }
                $passed = $chk -match ': passed'
                $icon = if ($passed) { "✓" } else { "✗" }
                $chkColor = if ($passed) { "Green" } else { "Red" }
                Write-Host "    $icon $name" -ForegroundColor $chkColor
            }
        }
    }

    # --- Full leaderboard with resolved task names ---
    Write-Host ""
    Write-Host "Leaderboard (all tasks):" -ForegroundColor Cyan
    $sortedLb = $leaderboardData | Sort-Object tx_task_id
    foreach ($t in $sortedLb) {
        $name = if ($taskMapping.ContainsKey($t.tx_task_id)) { $taskMapping[$t.tx_task_id] } else { "?" }
        $prevScore = if ($preScores.ContainsKey($t.tx_task_id)) { $preScores[$t.tx_task_id] } else { 0 }
        $delta = [Math]::Round($t.best_score - $prevScore, 4)
        $deltaStr = if ($delta -gt 0) { " +$delta" } elseif ($delta -lt 0) { " $delta" } else { "" }
        $color = if ($delta -gt 0) { "Green" } elseif ($delta -lt 0) { "Red" } elseif ($t.best_score -eq 0) { "DarkGray" } else { "Gray" }
        Write-Host ("  tx:{0} {1,-26} best:{2,7}  attempts:{3}{4}" -f `
                $t.tx_task_id, $name, $t.best_score, $t.total_attempts, $deltaStr) -ForegroundColor $color
    }
}
catch {
    Write-Host "WARNING: Failed to fetch leaderboard: $_" -ForegroundColor Yellow
}

# --- Replay new competition requests locally ---
if ($NoReplay -or -not (Test-Path $submissionsFile)) { return }

$allLines = Get-Content $submissionsFile
$newLines = $allLines | Select-Object -Skip $preLineCount
if (-not $newLines -or $newLines.Count -eq 0) {
    Write-Host ""
    Write-Host "No new entries in submissions.jsonl to replay." -ForegroundColor Gray
    return
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Replaying $($newLines.Count) competition request(s) locally" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

foreach ($line in $newLines) {
    try {
        $entry = $line | ConvertFrom-Json
        $prompt = $entry.prompt
        if (-not $prompt -or $prompt -eq "ping") { continue }

        Write-Host ""
        Write-Host "--- Task: $($entry.task_type) ($($entry.language)) ---" -ForegroundColor Yellow
        Write-Host "Prompt: $($prompt.Substring(0, [Math]::Min(80, $prompt.Length)))..." -ForegroundColor Gray

        # Replay via Test-Solve.ps1
        & "$PSScriptRoot\Test-Solve.ps1" -Prompt $prompt

        # Summary of competition vs local
        Write-Host ""
        Write-Host "Competition result:" -ForegroundColor Cyan
        Write-Host "  Handler:    $($entry.handler)"
        Write-Host "  Success:    $($entry.success)"
        Write-Host "  API calls:  $($entry.call_count)"
        Write-Host "  Errors:     $($entry.error_count)"
        if ($entry.error) {
            Write-Host "  Error:      $($entry.error)" -ForegroundColor Red
        }
    }
    catch {
        Write-Host "  Failed to parse/replay entry: $_" -ForegroundColor Red
    }
}