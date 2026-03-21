<#
.SYNOPSIS
    Analyze competition results and explain failed checks using Tripletex help articles.
.DESCRIPTION
    Fetches the latest (or specified) submission result from the competition API,
    identifies failed checks, cross-references with local results.jsonl / submissions.jsonl
    for exact task context when available, and searches Tripletex help articles (Zendesk)
    for guidance on how to fix failures.
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

function Read-JsonLines {
    param([string]$Path)

    $items = @()
    if (-not (Test-Path $Path)) { return $items }

    foreach ($line in (Get-Content $Path -Encoding UTF8)) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $items += ($line | ConvertFrom-Json)
            }
        }
        catch { }
    }

    return $items
}

function Get-CheckName {
    param([string]$CheckText)

    if ([string]::IsNullOrWhiteSpace($CheckText)) { return $null }
    return (($CheckText -replace ':\s*(passed|failed|not found|missing|false|0).*$','').Trim())
}

function Get-KnownChecks {
    param([string]$TaskType)

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

    if ($knownChecks.ContainsKey($TaskType)) {
        return $knownChecks[$TaskType]
    }

    return @()
}

function Resolve-CheckTaskType {
    param(
        [string]$CheckText,
        [hashtable]$CheckToTaskType,
        [object[]]$ExactTasks
    )

    $checkName = Get-CheckName $CheckText
    if (-not $checkName) { return "unknown" }

    if ($checkName -match '^Check\s+(\d+)$' -and $ExactTasks.Count -eq 1) {
        $taskType = "$($ExactTasks[0].task_type)"
        $knownForTask = Get-KnownChecks $taskType
        $index = [int]$Matches[1] - 1
        if ($index -ge 0 -and $index -lt $knownForTask.Count) {
            $checkName = $knownForTask[$index]
        }
    }

    foreach ($key in $CheckToTaskType.Keys) {
        if ($checkName -match [regex]::Escape($key)) {
            return $CheckToTaskType[$key]
        }
    }

    if ($ExactTasks.Count -eq 1) {
        return "$($ExactTasks[0].task_type)"
    }

    return "unknown"
}

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

# --- Load local correlation data ---
$logsDir = Join-Path $PSScriptRoot "..\src\logs"
$resultsFile = Join-Path $logsDir "results.jsonl"
$submissionsFile = Join-Path $PSScriptRoot "..\src\logs\submissions.jsonl"
$allResults = Read-JsonLines $resultsFile
$matchedResult = $allResults | Where-Object { $_.submission_id -eq $sub.id } | Select-Object -Last 1
$competitionEntries = Read-JsonLines $submissionsFile | Where-Object {
    $_.environment -eq "competition" -and $_.prompt -ne "ping"
}
$exactTasks = @()
$runScopedEntries = @()
$correlationMode = "heuristic"

if ($matchedResult -and $matchedResult.tasks) {
    $exactTasks = @($matchedResult.tasks)
    $correlationMode = "results.jsonl"
}

if ($matchedResult -and $matchedResult.run_id) {
    $runScopedEntries = @($competitionEntries | Where-Object { $_.run_id -eq $matchedResult.run_id })
    if ($runScopedEntries.Count -gt 0) {
        $correlationMode = "results.jsonl + submissions.jsonl"
    }
}

Write-Host "Local correlation: $correlationMode" -ForegroundColor Gray
if ($matchedResult) {
    Write-Host "  Local result found for submission: $($matchedResult.submission_id)" -ForegroundColor Gray
    if ($matchedResult.run_id) {
        Write-Host "  run_id: $($matchedResult.run_id)" -ForegroundColor Gray
    }
    Write-Host "  Logged tasks: $(@($exactTasks).Count)" -ForegroundColor Gray
}
else {
    Write-Host "  No exact results.jsonl match for submission; falling back to task-type heuristics." -ForegroundColor Yellow
}
Write-Host ""

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
    $taskType = Resolve-CheckTaskType -CheckText $f -CheckToTaskType $checkToTaskType -ExactTasks $exactTasks
    
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
    
    $matchingTasks = @($exactTasks | Where-Object { $_.task_type -eq $taskType })
    $matchingEntries = @()
    if ($runScopedEntries.Count -gt 0) {
        $matchingEntries = @($runScopedEntries | Where-Object { $_.task_type -eq $taskType })
    }
    elseif ($competitionEntries.Count -gt 0) {
        $matchingEntries = @($competitionEntries | Where-Object { $_.task_type -eq $taskType } | Select-Object -Last 3)
    }

    if ($matchingTasks.Count -gt 0) {
        Write-Host "  Exact task context:" -ForegroundColor Cyan
        foreach ($task in $matchingTasks) {
            $taskLabel = if ($null -ne $task.task_index) { "$($task.task_index)" } else { "?" }
            $promptText = "$($task.prompt)"
            $promptPreview = if ($promptText.Length -gt 100) { $promptText.Substring(0, 100) + "..." } else { $promptText }
            Write-Host "    Task $taskLabel | Handler: $($task.handler) | Calls: $($task.call_count) | Errors: $($task.error_count)" -ForegroundColor Gray
            Write-Host "    Prompt: $promptPreview" -ForegroundColor Gray
            if ($task.error) {
                Write-Host "    Error: $($task.error)" -ForegroundColor Magenta
            }

            $failingCalls = @($task.api_calls | Where-Object { $_.Status -ge 400 })
            foreach ($call in ($failingCalls | Select-Object -First 2)) {
                Write-Host "    Failing call: $($call.Method) $($call.Path) -> $($call.Status)" -ForegroundColor DarkRed
                if ($call.Error) {
                    Write-Host "      $($call.Error)" -ForegroundColor DarkRed
                }
            }
        }
    }
    elseif ($matchingEntries.Count -gt 0) {
        Write-Host "  Heuristic log context:" -ForegroundColor DarkYellow
        foreach ($entry in $matchingEntries) {
            $promptText = "$($entry.prompt)"
            $promptPreview = if ($promptText.Length -gt 100) { $promptText.Substring(0, 100) + "..." } else { $promptText }
            Write-Host "    Handler: $($entry.handler) | Calls: $($entry.call_count) | Errors: $($entry.error_count)" -ForegroundColor Gray
            Write-Host "    Prompt: $promptPreview" -ForegroundColor Gray
            if ($entry.error) {
                Write-Host "    Error: $($entry.error)" -ForegroundColor Magenta
            }
        }
    }
    else {
        Write-Host "  No local task log context matched this task type." -ForegroundColor Yellow
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
Write-Host "  Correlation: $correlationMode" -ForegroundColor Gray

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
