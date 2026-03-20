<#
.SYNOPSIS
    Analyze competition results and explain failed checks using Tripletex help articles.
.DESCRIPTION
    Fetches the latest (or specified) submission result from the competition API,
    identifies failed checks, cross-references with submissions.jsonl for error context,
    and searches Tripletex help articles (Zendesk) for guidance on how to fix failures.
.PARAMETER Token
    The access_token cookie value. Falls back to $env:AINM_TOKEN or user-secrets.
.PARAMETER SubmissionId
    Specific submission ID to analyze. If omitted, analyzes the most recent submission.
.PARAMETER SkipHelp
    Skip the Zendesk help article search (faster output, no external calls).
#>
param(
    [string]$Token,
    [string]$SubmissionId,
    [switch]$SkipHelp
)

$ErrorActionPreference = "Stop"
$apiBase = "https://api.ainm.no"

# --- Resolve auth token ---
if (-not $Token) { $Token = $env:AINM_TOKEN }
if (-not $Token) {
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
    Write-Host "No auth token. Set `$env:AINM_TOKEN, pass -Token, or configure AinmToken in user-secrets." -ForegroundColor Red
    return
}

$headers = @{ "Cookie" = "access_token=$Token" }

# --- Fetch submissions from API ---
Write-Host "Fetching submissions from competition API..." -ForegroundColor Cyan
try {
    $allSubs = Invoke-RestMethod -Uri "$apiBase/tripletex/my/submissions" -Method GET -Headers $headers -ErrorAction Stop
}
catch {
    Write-Host "ERROR: Failed to fetch submissions: $_" -ForegroundColor Red
    return
}

# --- Select the submission to analyze ---
if ($SubmissionId) {
    $sub = $allSubs | Where-Object { $_.id -eq $SubmissionId } | Select-Object -First 1
    if (-not $sub) {
        Write-Host "ERROR: Submission $SubmissionId not found." -ForegroundColor Red
        return
    }
}
else {
    # Pick the most recent completed submission
    $sub = $allSubs | Where-Object { $_.status -eq "completed" } | Select-Object -First 1
    if (-not $sub) {
        $sub = $allSubs | Select-Object -First 1
    }
}

if (-not $sub) {
    Write-Host "No submissions found." -ForegroundColor Yellow
    return
}

# --- Display submission overview ---
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Submission Analysis" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ID:          $($sub.id)"
Write-Host "  Status:      $($sub.status)"
Write-Host "  Score:       $($sub.score_raw) / $($sub.score_max)" -ForegroundColor $(if ($sub.score_raw -eq $sub.score_max) { "Green" } else { "Yellow" })
Write-Host "  Normalized:  $($sub.normalized_score)"
Write-Host "  Created:     $($sub.created_at)"
Write-Host ""

# --- Parse checks ---
$passed = @()
$failed = @()

if ($sub.feedback -and $sub.feedback.checks) {
    foreach ($chk in $sub.feedback.checks) {
        $chkText = "$chk"
        if ($chkText -match ': *passed') {
            $passed += $chkText
        }
        else {
            $failed += $chkText
        }
    }
}

if ($sub.feedback -and $sub.feedback.comment) {
    Write-Host "Feedback: $($sub.feedback.comment)" -ForegroundColor Gray
    Write-Host ""
}

# --- Show passed checks ---
if ($passed.Count -gt 0) {
    Write-Host "PASSED ($($passed.Count)):" -ForegroundColor Green
    foreach ($p in $passed) {
        Write-Host "  [OK] $p" -ForegroundColor Green
    }
    Write-Host ""
}

if ($failed.Count -eq 0) {
    Write-Host "All checks passed! No failures to analyze." -ForegroundColor Green
    return
}

# --- Show failed checks ---
Write-Host "FAILED ($($failed.Count)):" -ForegroundColor Red
foreach ($f in $failed) {
    Write-Host "  [FAIL] $f" -ForegroundColor Red
}
Write-Host ""

# --- Cross-reference with submissions.jsonl ---
$submissionsFile = Join-Path $PSScriptRoot "..\src\logs\submissions.jsonl"
$competitionEntries = @()
if (Test-Path $submissionsFile) {
    $allLines = Get-Content $submissionsFile
    foreach ($line in $allLines) {
        try {
            $entry = $line | ConvertFrom-Json
            if ($entry.environment -eq "competition" -and $entry.prompt -ne "ping") {
                $competitionEntries += $entry
            }
        }
        catch { }
    }
}

# --- Map check names to task types ---
# Check names typically follow the pattern: task_description_check (e.g., "employee_found", "has_customer")
$checkToTaskType = @{
    "employee_found"          = "create_employee"
    "firstName"               = "create_employee"
    "lastName"                = "create_employee"
    "admin_role"              = "create_employee"
    "customer_found"          = "create_customer"
    "organizationNumber"      = "create_customer"
    "phoneNumber"             = "create_customer"
    "supplier_found"          = "create_supplier"
    "product_found"           = "create_product"
    "department_found"        = "create_department"
    "departmentNumber"        = "create_department"
    "project_found"           = "create_project"
    "has_project_manager"     = "create_project"
    "invoice_found"           = "create_invoice"
    "has_amount"              = "create_invoice"
    "correct_amount"          = "create_invoice"
    "payment_registered"      = "register_payment"
    "salary_transaction_found" = "run_payroll"
    "payslip_generated"       = "run_payroll"
    "travel_expense_found"    = "create_travel_expense"
    "has_title"               = "create_travel_expense"
    "has_employee"            = "create_travel_expense"
    "has_costs"               = "create_travel_expense"
    "credit_note_created"     = "create_credit_note"
    "voucher_found"           = "create_voucher"
    "has_description"         = "create_voucher"
    "has_postings"            = "create_voucher"
}

# Map task types to Norwegian search terms for Zendesk
$taskTypeSearchTerms = @{
    "create_employee"       = "ansatt opprette"
    "create_customer"       = "kunde opprette registrere"
    "create_supplier"       = "leverandor opprette"
    "create_product"        = "produkt opprette"
    "create_department"     = "avdeling opprette"
    "create_project"        = "prosjekt opprette prosjektleder"
    "create_invoice"        = "faktura opprette sende"
    "register_payment"      = "innbetaling betaling registrere faktura"
    "run_payroll"           = "lonn lonnskjoring utbetaling"
    "create_travel_expense" = "reiseregning utlegg diett"
    "create_credit_note"    = "kreditnota kreditere"
    "create_voucher"        = "bilag postering regnskap"
    "delete_entity"         = "slette"
    "enable_module"         = "modul aktivere"
}

# --- Zendesk search helper ---
function Search-TripletexHelp {
    param([string]$Query, [int]$MaxResults = 3)
    
    $url = "https://hjelp.tripletex.no/api/v2/help_center/articles/search.json?query=$([uri]::EscapeDataString($Query))&per_page=$MaxResults"
    try {
        $resp = Invoke-RestMethod -Uri $url -Method GET -TimeoutSec 5 -ErrorAction Stop
        $articles = @()
        foreach ($art in $resp.results) {
            $snippet = $art.snippet -replace '<[^>]+>', ''
            if ($snippet.Length -gt 300) { $snippet = $snippet.Substring(0, 300) + "..." }
            $articles += @{
                title   = $art.title
                snippet = $snippet.Trim()
                url     = $art.html_url
            }
        }
        return $articles
    }
    catch {
        return @()
    }
}

# --- Analyze each failed check ---
Write-Host "========================================" -ForegroundColor Yellow
Write-Host " Failure Analysis" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
Write-Host ""

# Group failed checks by inferred task type
$failedByTask = @{}
foreach ($f in $failed) {
    # Extract check name (before ": failed")
    $checkName = ($f -replace ':\s*(failed|not found|missing|false|0).*$', '').Trim()
    
    # Try to match to a task type
    $taskType = $null
    foreach ($key in $checkToTaskType.Keys) {
        if ($checkName -match $key) {
            $taskType = $checkToTaskType[$key]
            break
        }
    }
    
    if (-not $taskType) { $taskType = "unknown" }
    
    if (-not $failedByTask.ContainsKey($taskType)) {
        $failedByTask[$taskType] = @()
    }
    $failedByTask[$taskType] += $f
}

foreach ($taskType in $failedByTask.Keys | Sort-Object) {
    $checks = $failedByTask[$taskType]
    
    Write-Host "--- $taskType ---" -ForegroundColor Yellow
    foreach ($c in $checks) {
        Write-Host "  [FAIL] $c" -ForegroundColor Red
    }
    
    # Find matching competition entries
    $matchingEntries = $competitionEntries | Where-Object { $_.task_type -eq $taskType }
    if ($matchingEntries) {
        foreach ($entry in $matchingEntries) {
            $promptPreview = if ($entry.prompt.Length -gt 100) { $entry.prompt.Substring(0, 100) + "..." } else { $entry.prompt }
            Write-Host "  Prompt: $promptPreview" -ForegroundColor Gray
            Write-Host "  Handler: $($entry.handler) | Calls: $($entry.call_count) | Errors: $($entry.error_count)" -ForegroundColor Gray
            if ($entry.error) {
                Write-Host "  API Error: $($entry.error)" -ForegroundColor Magenta
            }
        }
    }
    
    # Search Zendesk for help articles
    if (-not $SkipHelp) {
        $searchQuery = $taskTypeSearchTerms[$taskType]
        if (-not $searchQuery) { $searchQuery = $taskType -replace '_', ' ' }
        
        $articles = Search-TripletexHelp -Query $searchQuery -MaxResults 2
        if ($articles.Count -gt 0) {
            Write-Host "  Help articles:" -ForegroundColor Cyan
            foreach ($art in $articles) {
                Write-Host "    - $($art.title)" -ForegroundColor Cyan
                Write-Host "      $($art.url)" -ForegroundColor DarkCyan
                if ($art.snippet) {
                    Write-Host "      $($art.snippet)" -ForegroundColor DarkGray
                }
            }
        }
    }
    
    Write-Host ""
}

# --- Summary ---
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Score:    $($sub.score_raw) / $($sub.score_max)"
Write-Host "  Passed:   $($passed.Count) checks" -ForegroundColor Green
Write-Host "  Failed:   $($failed.Count) checks" -ForegroundColor Red
Write-Host "  Tasks:    $($failedByTask.Keys.Count) task types with failures"

# --- Recommendations ---
$taskTypes = $failedByTask.Keys | Sort-Object
if ($taskTypes.Count -gt 0) {
    Write-Host ""
    Write-Host "Priority fixes (by task type):" -ForegroundColor Yellow
    $priority = 1
    foreach ($tt in $taskTypes) {
        $failCount = $failedByTask[$tt].Count
        Write-Host "  $priority. $tt ($failCount failed checks)" -ForegroundColor Yellow
        $priority++
    }
}
Write-Host ""
