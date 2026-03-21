<#
.SYNOPSIS
    Analyze a competition run or sandbox test by run_id, submission_id, or latest entry.
.DESCRIPTION
    Reads submissions.jsonl / sandbox.jsonl, results.jsonl, and leaderboard.jsonl to answer:
    1) Which prompts were run?
    2) What ExtractionResult did the LLM produce?
    3) What Tripletex API requests/responses were generated?
    4) How many tasks passed or failed?
    5) What checks passed or failed?
.PARAMETER RunId
    Filter by run_id (first 8 hex chars of SHA256(session_token)).
.PARAMETER SubmissionId
    Filter by competition submission_id.
.PARAMETER Last
    Show the N most recent tasks (default: all matching).
.PARAMETER ShowApiCalls
    Include full API call details (method, path, status, request body, response snippet).
.PARAMETER ShowExtraction
    Include full LLM extraction details.
.PARAMETER ShowPrompt
    Include the full prompt text.
.PARAMETER Sandbox
    Read from sandbox.jsonl instead of submissions.jsonl.
#>
param(
    [string]$RunId,
    [string]$SubmissionId,
    [int]$Last = 0,
    [switch]$ShowApiCalls,
    [switch]$ShowExtraction,
    [switch]$ShowPrompt,
    [switch]$Sandbox
)

$logsDir = Join-Path $PSScriptRoot "..\src\logs"
$submissionsFile = Join-Path $logsDir (if ($Sandbox) { "sandbox.jsonl" } else { "submissions.jsonl" })
$resultsFile = Join-Path $logsDir "results.jsonl"
$leaderboardFile = Join-Path $logsDir "leaderboard.jsonl"

# --- Load submissions ---
$entries = @()
if (Test-Path $submissionsFile) {
    Get-Content $submissionsFile -Encoding UTF8 | ForEach-Object {
        try {
            $e = $_ | ConvertFrom-Json
            if ($e.prompt -ne "ping") { $entries += $e }
        }
        catch {}
    }
}

if ($entries.Count -eq 0) {
    Write-Host "No entries found in $submissionsFile" -ForegroundColor Yellow
    return
}

# --- Filter ---
if ($RunId) {
    $entries = $entries | Where-Object { $_.run_id -eq $RunId }
}
elseif (-not $SubmissionId -and $Last -eq 0) {
    # Default: show last run_id
    $lastEntry = $entries | Select-Object -Last 1
    if ($lastEntry.run_id) {
        $RunId = $lastEntry.run_id
        $entries = $entries | Where-Object { $_.run_id -eq $RunId }
    }
    else {
        $Last = 30  # Fallback: show last 30 entries
    }
}

if ($Last -gt 0) {
    $entries = $entries | Select-Object -Last $Last
}

$effectiveRunId = if ($RunId) { $RunId } elseif ($entries.Count -gt 0) { $entries[0].run_id } else { "?" }

# --- Summary header ---
Write-Host ""
Write-Host "=== Run Analysis ===" -ForegroundColor Cyan
Write-Host "  Run ID:     $effectiveRunId" -ForegroundColor Gray
Write-Host "  Source:     $($Sandbox ? 'sandbox' : 'competition')" -ForegroundColor Gray
Write-Host "  Tasks:      $($entries.Count)" -ForegroundColor Gray
$succeeded = ($entries | Where-Object { $_.success -eq $true }).Count
$failed = ($entries | Where-Object { $_.success -ne $true }).Count
Write-Host "  Succeeded:  $succeeded" -ForegroundColor Green
Write-Host "  Failed:     $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })
Write-Host ""

# --- Per-task detail ---
Write-Host "--- Tasks ---" -ForegroundColor Cyan
$taskNum = 0
foreach ($e in ($entries | Sort-Object { $_.task_index })) {
    $taskNum++
    $status = if ($e.success) { "[OK]" } else { "[FAIL]" }
    $statusColor = if ($e.success) { "Green" } else { "Red" }

    Write-Host ""
    Write-Host "$status Task $($e.task_index ?? $taskNum): $($e.task_type) ($($e.handler))" -ForegroundColor $statusColor
    Write-Host "    Elapsed: $($e.elapsed_ms)ms | API calls: $($e.call_count) | Errors: $($e.error_count)" -ForegroundColor Gray

    if ($e.error) {
        Write-Host "    Error: $($e.error)" -ForegroundColor Red
    }

    if ($e.entity_id) {
        Write-Host "    Entity ID: $($e.entity_id)" -ForegroundColor Gray
    }

    if ($ShowPrompt) {
        Write-Host "    Prompt:" -ForegroundColor Yellow
        $promptLines = ($e.prompt -split "`n") | Select-Object -First 5
        foreach ($pl in $promptLines) {
            Write-Host "      $pl" -ForegroundColor DarkGray
        }
        if (($e.prompt -split "`n").Count -gt 5) {
            Write-Host "      ... (truncated)" -ForegroundColor DarkGray
        }
    }

    if ($ShowExtraction -and $e.extraction) {
        Write-Host "    Extraction:" -ForegroundColor Yellow
        $exJson = $e.extraction | ConvertTo-Json -Depth 5 -Compress
        if ($exJson.Length -gt 200) { $exJson = $exJson.Substring(0, 200) + "..." }
        Write-Host "      $exJson" -ForegroundColor DarkGray
    }

    if ($ShowApiCalls -and $e.api_calls) {
        Write-Host "    API Calls:" -ForegroundColor Yellow
        foreach ($call in $e.api_calls) {
            $callStatus = if ($call.Status -ge 400) { "Red" } else { "DarkGray" }
            Write-Host "      $($call.Method) $($call.Path) → $($call.Status)" -ForegroundColor $callStatus
            if ($call.Error) {
                Write-Host "        Error: $($call.Error)" -ForegroundColor Red
            }
            if ($call.Status -ge 400 -and $call.ResponseSnippet) {
                $snippet = $call.ResponseSnippet
                if ($snippet.Length -gt 300) { $snippet = $snippet.Substring(0, 300) + "..." }
                Write-Host "        Response: $snippet" -ForegroundColor DarkRed
            }
        }
    }
}

# --- Competition results (if available) ---
if ($SubmissionId -or $effectiveRunId) {
    $results = @()
    if (Test-Path $resultsFile) {
        Get-Content $resultsFile -Encoding UTF8 | ForEach-Object {
            try { $results += ($_ | ConvertFrom-Json) } catch {}
        }
    }

    $matchedResult = $null
    if ($SubmissionId) {
        $matchedResult = $results | Where-Object { $_.submission_id -eq $SubmissionId } | Select-Object -Last 1
    }
    elseif ($effectiveRunId) {
        $matchedResult = $results | Where-Object { $_.run_id -eq $effectiveRunId } | Select-Object -Last 1
    }

    if ($matchedResult) {
        Write-Host ""
        Write-Host "--- Competition Result ---" -ForegroundColor Cyan
        Write-Host "  Submission: $($matchedResult.submission_id)" -ForegroundColor Gray
        Write-Host "  Score:      $($matchedResult.score_raw)/$($matchedResult.score_max)" -ForegroundColor Yellow
        Write-Host "  Checks:     $($matchedResult.passed_checks)/$($matchedResult.total_checks) passed" -ForegroundColor $(if ($matchedResult.failed_checks -gt 0) { "Yellow" } else { "Green" })

        if ($matchedResult.checks) {
            Write-Host ""
            Write-Host "  Checks detail:" -ForegroundColor Cyan
            foreach ($chk in $matchedResult.checks) {
                $chkColor = if ($chk.passed) { "Green" } else { "Red" }
                $chkMark = if ($chk.passed) { "✓" } else { "✗" }
                Write-Host "    $chkMark $($chk.text)" -ForegroundColor $chkColor
            }
        }
    }
}

# --- Leaderboard delta (if available) ---
if ($effectiveRunId -or $SubmissionId) {
    $leaderboard = @()
    if (Test-Path $leaderboardFile) {
        Get-Content $leaderboardFile -Encoding UTF8 | ForEach-Object {
            try { $leaderboard += ($_ | ConvertFrom-Json) } catch {}
        }
    }

    $matchedLb = $null
    if ($SubmissionId) {
        $matchedLb = $leaderboard | Where-Object { $_.submission_id -eq $SubmissionId } | Select-Object -Last 1
    }
    elseif ($effectiveRunId) {
        $matchedLb = $leaderboard | Where-Object { $_.run_id -eq $effectiveRunId } | Select-Object -Last 1
    }

    if ($matchedLb -and $matchedLb.score_changes) {
        Write-Host ""
        Write-Host "--- Score Changes ---" -ForegroundColor Cyan
        Write-Host "  Total best: $($matchedLb.total_best_score)" -ForegroundColor Yellow
        foreach ($sc in $matchedLb.score_changes) {
            $arrow = if ($sc.delta -gt 0) { "↑" } elseif ($sc.delta -lt 0) { "↓" } else { "→" }
            $color = if ($sc.delta -gt 0) { "Green" } elseif ($sc.delta -lt 0) { "Red" } else { "Gray" }
            Write-Host "    $arrow $($sc.tx_task_id): $($sc.prev_score) → $($sc.new_score) ($($sc.delta))" -ForegroundColor $color
        }
    }
}

Write-Host ""
