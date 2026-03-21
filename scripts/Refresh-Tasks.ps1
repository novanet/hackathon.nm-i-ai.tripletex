<#
.SYNOPSIS
    Auto-generates per-task markdown files (prompts.md, runs.md, history.md)
    from JSONL log files.

.DESCRIPTION
    Parses submissions.jsonl, sandbox.jsonl, results.jsonl, and validations.jsonl
    to produce up-to-date task-specific documentation in each task folder under tasks/.

    Files generated per task folder:
      - prompts.md   — All known prompts (competition + sandbox), deduplicated
      - runs.md      — Latest competition + sandbox results, API calls, errors
      - history.md   — Score progression across submissions

    Run after every competition submission or local test session.

.EXAMPLE
    .\scripts\Refresh-Tasks.ps1
#>

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$logsDir = Join-Path $root "src/logs"
$tasksDir = Join-Path $root "tasks"

# --- Task ID → folder mapping ---
$taskFolders = @{
    "01" = "01-create-employee-basic"
    "02" = "02-create-customer"
    "03" = "03-create-product"
    "04" = "04-create-supplier"
    "05" = "05-create-department-multi"
    "06" = "06-create-invoice-simple"
    "07" = "07-register-payment-simple"
    "08" = "08-create-project-basic"
    "09" = "09-create-invoice-multiline"
    "10" = "10-register-payment-create-pay"
    "11" = "11-create-voucher-supplier-inv"
    "12" = "12-run-payroll"
    "13" = "13-create-travel-expense"
    "14" = "14-create-credit-note"
    "15" = "15-create-project-fixed-price"
    "16" = "16-create-project-timesheet"
    "17" = "17-create-voucher-dimension"
    "18" = "18-register-payment-full-chain"
    "19" = "19-create-employee-pdf-contract"
    "20" = "20-create-voucher-pdf-supplier"
    "21" = "21-create-employee-pdf-offer"
    "22" = "22-create-voucher-pdf-receipt"
    "23" = "23-bank-reconciliation-csv"
    "24" = "24-create-voucher-ledger-correction"
    "25" = "25-register-payment-overdue-reminder"
    "26" = "26-unknown"
    "27" = "27-register-payment-fx-eur"
    "28" = "28-create-project-cost-analysis"
    "29" = "29-create-project-lifecycle"
    "30" = "30-create-voucher-annual-accounts"
}

# Task type + variant keywords → task ID (for matching submissions to task IDs)
$taskTypeToIds = @{
    # Simple mappings (unique task types)
    "create_customer"       = @("02")
    "create_product"        = @("03")
    "create_supplier"       = @("04")
    "create_department"     = @("05")
    "run_payroll"           = @("12")
    "create_travel_expense" = @("13")
    "create_credit_note"    = @("14")
    "bank_reconciliation"   = @("23")
    "set_fixed_price"       = @("15")
}

# --- Helper: Parse JSONL file ---
function Read-Jsonl {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return @() }
    Get-Content $Path | Where-Object { $_.Trim() -ne "" } | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { }
    }
}

# --- Helper: Identify task ID from a submission entry ---
function Get-TaskIdFromPrompt {
    param(
        [string]$Prompt,
        [bool]$HasFiles = $false
    )

    if ([string]::IsNullOrWhiteSpace($Prompt)) { return $null }

    # Task 25: overdue invoice + reminder fee + invoice/payment workflow.
    if ($Prompt -match "retard|overdue|forfalt|forfalte|überfällig|uberfallig|vencid|rappel|reminder|purring|purregebyr|mahngeb[uü]hr|recordatorio") {
        return "25"
    }

    return $null
}

function Get-TaskId {
    param($Entry)
    
    $type = $Entry.task_type
    $hasFiles = $Entry.files -and $Entry.files.Count -gt 0
    $prompt = $Entry.prompt

    if (-not $type -or $type -eq "unknown") {
        return Get-TaskIdFromPrompt -Prompt $prompt -HasFiles:$hasFiles
    }
    
    # Unique task types → direct mapping
    if ($taskTypeToIds.ContainsKey($type)) {
        return $taskTypeToIds[$type][0]
    }

    if ($type -in @("reminder_fee", "overdue_invoice_reminder")) {
        return "25"
    }
    
    # Multi-variant types need heuristics
    $promptTaskId = Get-TaskIdFromPrompt -Prompt $prompt -HasFiles:$hasFiles
    if ($promptTaskId) { return $promptTaskId }
    
    switch ($type) {
        "create_employee" {
            if ($hasFiles -and $prompt -match "carta de oferta|offer letter") { return "21" }
            if ($hasFiles) { return "19" }
            return "01"
        }
        "create_invoice" {
            # Multi-line if prompt mentions multiple items/lines/products
            if ($prompt -match "linje|line|línea|ligne|Zeile|produto" -and $prompt -match "\d+\s*(stk|x|×|units)") { return "09" }
            # Check for multiple orderLines in extraction
            if ($Entry.extraction -and $Entry.extraction.entities) {
                $orderLines = $Entry.extraction.entities | Where-Object { $_.PSObject.Properties.Name -contains "orderLines" }
                if ($orderLines -and $orderLines.orderLines.Count -gt 1) { return "09" }
            }
            return "06"
        }
        "register_payment" {
            if ($prompt -match "EUR|USD|valuta|currency|devise|Währung|câmbio|veksel") { return "27" }
            # Full chain = needs to create customer + order + invoice + pay
            if ($prompt -match "kunde|customer|client|Kunde|cliente" -and $prompt -match "faktura|invoice|facture|Rechnung|fatura") { return "18" }
            if ($prompt -match "opprett|create|créer|erstellen|criar" -and $prompt -match "betal|pay|payer|zahlen|pagar") { return "10" }
            return "07"
        }
        "create_project" {
            if ($prompt -match "kostnad|cost|coût|Kosten|custo|auka|increase") { return "28" }
            if ($prompt -match "livssyklus|lifecycle|lebenszyklus|ciclo|cycle de vie|timer.*faktura|hours.*invoice") { return "29" }
            if ($prompt -match "fastpris|fixed.price|prix fixe|Festpreis|preço fixo") { return "15" }
            if ($prompt -match "timer|timesheet|hours|heures|Stunden|horas") { return "16" }
            return "08"
        }
        "create_voucher" {
            if ($hasFiles -and $prompt -match "kvittering|receipt|reçu|Quittung|recibo") { return "22" }
            if ($hasFiles -and $prompt -match "leverandør|supplier|fournisseur|Lieferant|fornecedor|facture|faktura|invoice|Rechnung") { return "20" }
            if ($prompt -match "årsoppgjer|annual|annuel|Jahres|anual") { return "30" }
            if ($prompt -match "korriger|correct|corriger|korrigieren|corrigir|Hauptbuch|ledger|grand livre") { return "24" }
            if ($prompt -match "dimensjon|dimension|Dimension|dimensão") { return "17" }
            return "11"
        }
    }
    
    return $null
}

# --- Helper: Truncate string ---
function Truncate {
    param([string]$Text, [int]$Max = 200)
    if (-not $Text) { return "" }
    if ($Text.Length -le $Max) { return $Text }
    return $Text.Substring(0, $Max) + "..."
}

# --- Helper: Write file only if content changed ---
$script:updatedFiles = 0
$script:skippedFiles = 0
function Write-IfChanged {
    param([string]$Path, [string]$Content)
    if (Test-Path $Path) {
        $existing = Get-Content $Path -Raw -Encoding UTF8
        # Normalize line endings and trailing whitespace for comparison
        $normExisting = ($existing -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd()
        $normContent = ($Content -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd()
        if ($null -ne $existing -and $normExisting -eq $normContent) {
            $script:skippedFiles++
            return
        }
    }
    Set-Content -Path $Path -Value $Content -Encoding UTF8 -NoNewline
    $script:updatedFiles++
}

# --- Load all data ---
Write-Host "Loading logs..."
$submissions = Read-Jsonl (Join-Path $logsDir "submissions.jsonl")
$sandbox = Read-Jsonl (Join-Path $logsDir "sandbox.jsonl")
$results = Read-Jsonl (Join-Path $logsDir "results.jsonl")
$validations = Read-Jsonl (Join-Path $logsDir "validations.jsonl")

Write-Host "  submissions: $($submissions.Count), sandbox: $($sandbox.Count), results: $($results.Count), validations: $($validations.Count)"

# --- Group submissions by task ID ---
$subsByTask = @{}
$sandByTask = @{}

foreach ($entry in $submissions) {
    $id = Get-TaskId $entry
    if ($id) {
        if (-not $subsByTask[$id]) { $subsByTask[$id] = @() }
        $subsByTask[$id] += $entry
    }
}

foreach ($entry in $sandbox) {
    $id = Get-TaskId $entry
    if ($id) {
        if (-not $sandByTask[$id]) { $sandByTask[$id] = @() }
        $sandByTask[$id] += $entry
    }
}

# --- Group results by task (results contain tasks array) ---
$resultsByTask = @{}
foreach ($result in $results) {
    if ($result.tasks) {
        foreach ($task in $result.tasks) {
            # Try to match task to an ID
            $fakeEntry = [pscustomobject]@{
                task_type  = $task.task_type
                prompt     = ""
                files      = @()
                extraction = $null
            }
            $id = Get-TaskId $fakeEntry
            if ($id) {
                if (-not $resultsByTask[$id]) { $resultsByTask[$id] = @() }
                $resultsByTask[$id] += [pscustomobject]@{
                    submission_id    = $result.submission_id
                    timestamp        = $result.timestamp
                    score_raw        = $result.score_raw
                    score_max        = $result.score_max
                    normalized_score = $result.normalized_score
                    checks           = $result.checks
                    task             = $task
                }
            }
        }
    }
}

# --- Generate files per task ---
$generated = 0
foreach ($id in ($taskFolders.Keys | Sort-Object)) {
    $folder = $taskFolders[$id]
    $dir = Join-Path $tasksDir $folder
    if (-not (Test-Path $dir)) { continue }
    
    $subs = $subsByTask[$id]
    $sands = $sandByTask[$id]
    $taskResults = $resultsByTask[$id]
    
    # ========== prompts.md ==========
    $promptSet = [System.Collections.Generic.HashSet[string]]::new()
    $promptEntries = @()
    
    foreach ($entry in $subs) {
        $p = $entry.prompt
        if ($p -and $promptSet.Add($p)) {
            $hasFiles = $entry.files -and $entry.files.Count -gt 0
            $fileInfo = if ($hasFiles) { " (+ $($entry.files.Count) file(s))" } else { "" }
            $promptEntries += [pscustomobject]@{
                Source    = "competition"
                Timestamp = $entry.timestamp
                Prompt    = $p
                FileInfo  = $fileInfo
                Language  = $entry.language
            }
        }
    }
    foreach ($entry in $sands) {
        $p = $entry.prompt
        if ($p -and $promptSet.Add($p)) {
            $hasFiles = $entry.files -and $entry.files.Count -gt 0
            $fileInfo = if ($hasFiles) { " (+ $($entry.files.Count) file(s))" } else { "" }
            $promptEntries += [pscustomobject]@{
                Source    = "sandbox"
                Timestamp = $entry.timestamp
                Prompt    = $p
                FileInfo  = $fileInfo
                Language  = $entry.language
            }
        }
    }
    
    $promptMd = "# prompts — task $id`n`n"
    $promptMd += "*Auto-generated by Refresh-Tasks.ps1*`n`n"
    
    if ($promptEntries.Count -eq 0) {
        $promptMd += "No prompts recorded yet.`n"
    }
    else {
        $promptMd += "$($promptEntries.Count) unique prompt(s) found.`n`n"
        $i = 0
        foreach ($pe in $promptEntries) {
            $i++
            $lang = if ($pe.Language) { " | $($pe.Language)" } else { "" }
            $promptMd += "## Prompt $i ($($pe.Source)$lang)$($pe.FileInfo)`n`n"
            $promptMd += "``````text`n$($pe.Prompt)`n```````n`n"
        }
    }
    
    Write-IfChanged -Path (Join-Path $dir "prompts.md") -Content $promptMd
    
    # ========== runs.md ==========
    $runsMd = "# runs — task $id`n`n"
    $runsMd += "*Auto-generated by Refresh-Tasks.ps1*`n`n"
    
    # Latest competition run
    $latestComp = $subs | Sort-Object { $_.timestamp } | Select-Object -Last 1
    if ($latestComp) {
        $runsMd += "## Latest Competition Run`n`n"
        $runsMd += "| Field | Value |`n|---|---|`n"
        $runsMd += "| Timestamp | $($latestComp.timestamp) |`n"
        $runsMd += "| Task Type | ``$($latestComp.task_type)`` |`n"
        $runsMd += "| Handler | ``$($latestComp.handler)`` |`n"
        $runsMd += "| Success | $($latestComp.success) |`n"
        $runsMd += "| Elapsed | $($latestComp.elapsed_ms) ms |`n"
        $runsMd += "| API Calls | $($latestComp.call_count) |`n"
        $runsMd += "| Errors | $($latestComp.error_count) |`n"
        if ($latestComp.error) {
            $runsMd += "| Error | ``$(Truncate $latestComp.error 150)`` |`n"
        }
        $runsMd += "`n"
        
        # API call details
        if ($latestComp.api_calls -and $latestComp.api_calls.Count -gt 0) {
            $runsMd += "### API Calls`n`n"
            $runsMd += "| # | Method | Path | Status | Time |`n|---|---|---|---|---|`n"
            $i = 0
            foreach ($call in $latestComp.api_calls) {
                $i++
                $method = $call.method
                $path = Truncate $call.path 60
                $status = $call.status_code
                $statusIcon = if ($status -ge 400) { "❌ $status" } else { "✅ $status" }
                $time = if ($call.elapsed_ms) { "$($call.elapsed_ms)ms" } else { "" }
                $runsMd += "| $i | ``$method`` | ``$path`` | $statusIcon | $time |`n"
            }
            $runsMd += "`n"
            
            # Show error responses
            $errors = $latestComp.api_calls | Where-Object { $_.status_code -ge 400 }
            if ($errors) {
                $runsMd += "### Error Responses`n`n"
                foreach ($err in $errors) {
                    $runsMd += "**$($err.method) $($err.path)** → $($err.status_code)`n`n"
                    if ($err.response_body) {
                        $runsMd += "``````json`n$(Truncate $err.response_body 500)`n```````n`n"
                    }
                }
            }
        }
        
        # Extraction
        if ($latestComp.extraction) {
            $runsMd += "### LLM Extraction`n`n"
            $runsMd += "``````json`n$($latestComp.extraction | ConvertTo-Json -Depth 5 -Compress)`n```````n`n"
        }
    }
    else {
        $runsMd += "## Latest Competition Run`n`nNo competition runs recorded.`n`n"
    }
    
    # Latest sandbox run
    $latestSand = $sands | Sort-Object { $_.timestamp } | Select-Object -Last 1
    if ($latestSand) {
        $runsMd += "## Latest Sandbox Run`n`n"
        $runsMd += "| Field | Value |`n|---|---|`n"
        $runsMd += "| Timestamp | $($latestSand.timestamp) |`n"
        $runsMd += "| Task Type | ``$($latestSand.task_type)`` |`n"
        $runsMd += "| Handler | ``$($latestSand.handler)`` |`n"
        $runsMd += "| Success | $($latestSand.success) |`n"
        $runsMd += "| Elapsed | $($latestSand.elapsed_ms) ms |`n"
        $runsMd += "| API Calls | $($latestSand.call_count) |`n"
        $runsMd += "| Errors | $($latestSand.error_count) |`n"
        if ($latestSand.error) {
            $runsMd += "| Error | ``$(Truncate $latestSand.error 150)`` |`n"
        }
        $runsMd += "`n"
        
        if ($latestSand.api_calls -and $latestSand.api_calls.Count -gt 0) {
            $runsMd += "### API Calls`n`n"
            $runsMd += "| # | Method | Path | Status | Time |`n|---|---|---|---|---|`n"
            $i = 0
            foreach ($call in $latestSand.api_calls) {
                $i++
                $method = $call.method
                $path = Truncate $call.path 60
                $status = $call.status_code
                $statusIcon = if ($status -ge 400) { "❌ $status" } else { "✅ $status" }
                $time = if ($call.elapsed_ms) { "$($call.elapsed_ms)ms" } else { "" }
                $runsMd += "| $i | ``$method`` | ``$path`` | $statusIcon | $time |`n"
            }
            $runsMd += "`n"
        }
    }
    
    # Latest validation
    $taskValidations = $validations | Where-Object { 
        $fakeEntry = [pscustomobject]@{ task_type = $_.task_type; prompt = $_.prompt; files = @(); extraction = $null }
        $vid = Get-TaskId $fakeEntry
        $vid -eq $id
    }
    $latestVal = $taskValidations | Sort-Object { $_.timestamp } | Select-Object -Last 1
    if ($latestVal) {
        $runsMd += "## Latest Local Validation`n`n"
        $runsMd += "| Field | Value |`n|---|---|`n"
        $runsMd += "| Correctness | $($latestVal.correctness) |`n"
        $runsMd += "| Points | $($latestVal.points_earned) / $($latestVal.max_points) |`n"
        $runsMd += "`n"
        
        if ($latestVal.checks) {
            $runsMd += "### Checks`n`n"
            $runsMd += "| Check | Expected | Actual | Passed | Points |`n|---|---|---|---|---|`n"
            foreach ($check in $latestVal.checks) {
                $icon = if ($check.passed) { "✅" } else { "❌" }
                $runsMd += "| $($check.field) | ``$($check.expected)`` | ``$($check.actual)`` | $icon | $($check.points) |`n"
            }
            $runsMd += "`n"
        }
    }
    
    Write-IfChanged -Path (Join-Path $dir "runs.md") -Content $runsMd
    
    # ========== history.md ==========
    $historyMd = "# history — task $id`n`n"
    $historyMd += "*Auto-generated by Refresh-Tasks.ps1*`n`n"
    
    # Collect all runs for this task with scores
    $allRuns = @()
    
    foreach ($entry in $subs) {
        $allRuns += [pscustomobject]@{
            Timestamp = $entry.timestamp
            Source    = "competition"
            Success   = $entry.success
            Handler   = $entry.handler
            Calls     = $entry.call_count
            Errors    = $entry.error_count
            ElapsedMs = $entry.elapsed_ms
        }
    }
    
    foreach ($entry in $sands) {
        $allRuns += [pscustomobject]@{
            Timestamp = $entry.timestamp
            Source    = "sandbox"
            Success   = $entry.success
            Handler   = $entry.handler
            Calls     = $entry.call_count
            Errors    = $entry.error_count
            ElapsedMs = $entry.elapsed_ms
        }
    }
    
    $allRuns = $allRuns | Sort-Object Timestamp
    
    if ($allRuns.Count -eq 0) {
        $historyMd += "No runs recorded.`n"
    }
    else {
        $historyMd += "$($allRuns.Count) total run(s).`n`n"
        $historyMd += "| # | Timestamp | Source | Success | Handler | Calls | Errors | Time |`n"
        $historyMd += "|---|---|---|---|---|---|---|---|`n"
        $i = 0
        foreach ($run in $allRuns) {
            $i++
            $icon = if ($run.Success) { "✅" } else { "❌" }
            $ts = if ($run.Timestamp) { $run.Timestamp.ToString().Substring(0, [Math]::Min(19, $run.Timestamp.ToString().Length)) } else { "" }
            $historyMd += "| $i | $ts | $($run.Source) | $icon | $($run.Handler) | $($run.Calls) | $($run.Errors) | $($run.ElapsedMs)ms |`n"
        }
    }
    
    Write-IfChanged -Path (Join-Path $dir "history.md") -Content $historyMd
    
    $generated++
}

# ================================================================
# PRIORITY_EXECUTION_ORDER.md — Auto-regenerated from leaderboard
# ================================================================

$leaderboardFile = Join-Path $logsDir "leaderboard.jsonl"
$leaderboard = Read-Jsonl $leaderboardFile
if ($leaderboard.Count -gt 0) {
    $latest = $leaderboard[-1]
    $ourTotal = [Math]::Round($latest.total_best_score, 2)
    $dateRaw = $latest.timestamp.ToString()
    # Parse ISO-8601 date regardless of locale
    $date = try { ([datetime]::Parse($dateRaw, [System.Globalization.CultureInfo]::InvariantCulture)).ToString("yyyy-MM-dd") } catch { (Get-Date -Format "yyyy-MM-dd") }

    # Build score lookup: tx_task_id → best_score
    $bestScores = @{}
    foreach ($t in $latest.tasks) {
        $bestScores[$t.tx_task_id] = [Math]::Round($t.best_score, 2)
    }

    # Task metadata: id → { tier, type, variant, leaderMax }
    # leaderMax = known leader best scores (manually maintained baseline — update from leaderboard screenshots)
    $taskMeta = @{
        "01" = @{ tier = 1; type = "create_employee"; variant = "Basic"; leaderMax = 2.00 }
        "02" = @{ tier = 1; type = "create_customer"; variant = "Standard"; leaderMax = 2.00 }
        "03" = @{ tier = 1; type = "create_product"; variant = "Standard"; leaderMax = 2.00 }
        "04" = @{ tier = 1; type = "create_supplier"; variant = "Standard"; leaderMax = 2.00 }
        "05" = @{ tier = 1; type = "create_department"; variant = "Multi"; leaderMax = 2.00 }
        "06" = @{ tier = 2; type = "create_invoice"; variant = "Simple"; leaderMax = 1.67 }
        "07" = @{ tier = 2; type = "register_payment"; variant = "Simple existing"; leaderMax = 2.00 }
        "08" = @{ tier = 2; type = "create_project"; variant = "Basic"; leaderMax = 2.00 }
        "09" = @{ tier = 2; type = "create_invoice"; variant = "Multi-line"; leaderMax = 4.00 }
        "10" = @{ tier = 2; type = "register_payment"; variant = "Create + pay"; leaderMax = 4.00 }
        "11" = @{ tier = 2; type = "create_voucher"; variant = "Supplier invoice"; leaderMax = 4.00 }
        "12" = @{ tier = 2; type = "run_payroll"; variant = "Standard"; leaderMax = 4.00 }
        "13" = @{ tier = 2; type = "create_travel_expense"; variant = "With costs"; leaderMax = 2.40 }
        "14" = @{ tier = 2; type = "create_credit_note"; variant = "Standard"; leaderMax = 4.00 }
        "15" = @{ tier = 2; type = "create_project"; variant = "Fixed-price"; leaderMax = 3.33 }
        "16" = @{ tier = 2; type = "create_project"; variant = "Timesheet hours"; leaderMax = 3.00 }
        "17" = @{ tier = 2; type = "create_voucher"; variant = "Custom dimension"; leaderMax = 3.50 }
        "18" = @{ tier = 2; type = "register_payment"; variant = "Full chain"; leaderMax = 4.00 }
        "19" = @{ tier = 3; type = "create_employee"; variant = "PDF contract (T3)"; leaderMax = 2.73 }
        "20" = @{ tier = 3; type = "create_voucher"; variant = "PDF supplier inv (T3)"; leaderMax = 2.40 }
        "21" = @{ tier = 3; type = "create_employee"; variant = "PDF offer letter (T3)"; leaderMax = 2.57 }
        "22" = @{ tier = 3; type = "create_voucher"; variant = "PDF receipt (T3)"; leaderMax = 0.00 }
        "23" = @{ tier = 3; type = "bank_reconciliation"; variant = "CSV (T3)"; leaderMax = 0.60 }
        "24" = @{ tier = 3; type = "create_voucher"; variant = "Ledger correction (T3)"; leaderMax = 2.25 }
        "25" = @{ tier = 3; type = "register_payment"; variant = "Overdue + reminder (T3)"; leaderMax = 6.00 }
        "26" = @{ tier = 3; type = "???"; variant = "Unknown"; leaderMax = 6.00 }
        "27" = @{ tier = 3; type = "register_payment"; variant = "FX/EUR (T3)"; leaderMax = 6.00 }
        "28" = @{ tier = 3; type = "create_project"; variant = "Cost analysis (T3)"; leaderMax = 1.50 }
        "29" = @{ tier = 3; type = "create_project"; variant = "Full lifecycle (T3)"; leaderMax = 2.73 }
        "30" = @{ tier = 3; type = "create_voucher"; variant = "Annual accounts (T3)"; leaderMax = 1.80 }
    }

    # Build rows with gap info
    $rows = @()
    foreach ($id in ($taskFolders.Keys | Sort-Object)) {
        $meta = $taskMeta[$id]
        $us = if ($bestScores.ContainsKey($id)) { $bestScores[$id] } else { 0 }
        $leader = $meta.leaderMax
        $gap = [Math]::Round($us - $leader, 2)
        $folder = $taskFolders[$id]

        $status = if ($gap -gt 0) { "✅ Leading" }
        elseif ($gap -eq 0 -and $us -gt 0) { "✅ Tied" }
        elseif ($gap -eq 0 -and $us -eq 0 -and $leader -eq 0) { "❌ Both fail" }
        elseif ($gap -ge -0.5) { "⚠️ Behind" }
        elseif ($us -eq 0) { "❌ Failing" }
        else { "❌ Failing" }

        $rows += [pscustomobject]@{
            Id = $id; Tier = $meta.tier; Type = $meta.type; Variant = $meta.variant
            Us = $us; Leader = $leader; Gap = $gap; Status = $status; Folder = $folder
        }
    }

    # Compute totals
    $leaderTotal = [Math]::Round(($rows | Measure-Object -Property Leader -Sum).Sum, 2)

    # Priority list: tasks where gap < 0, sorted by effort-to-gain
    $priorityRows = $rows | Where-Object { $_.Gap -lt 0 } | Sort-Object { $_.Gap }

    # Build markdown
    $md = "# Priority Execution Order`n`n"
    $md += "*Auto-generated by Refresh-Tasks.ps1*`n`n"
    $md += "**Current scores:** Us $ourTotal pts | Leader $leaderTotal pts | Gap $([Math]::Round($ourTotal - $leaderTotal, 2)) pts ($date)`n`n"

    # Priority table
    $md += "## Tasks to improve (sorted by gap)`n`n"
    $md += "| # | Task | Type | Variant | Us | Leader | Gap | Status | Folder |`n"
    $md += "|---|---|---|---|---:|---:|---:|---|---|`n"
    $i = 0
    foreach ($r in $priorityRows) {
        $i++
        $md += "| $i | $($r.Id) | ``$($r.Type)`` | $($r.Variant) | $($r.Us) | $($r.Leader) | $($r.Gap) | $($r.Status) | [$($r.Folder)]($($r.Folder)/) |`n"
    }

    # Cumulative gain milestones
    $cumGain = 0
    $md += "`n## Milestones`n`n"
    $md += "| After task # | Cumulative recoverable | Projected total |`n"
    $md += "|---|---|---|`n"
    $checkpoints = @(3, 5, 8, $priorityRows.Count)
    foreach ($cp in $checkpoints) {
        if ($cp -gt $priorityRows.Count) { continue }
        $cumGain = [Math]::Round(($priorityRows | Select-Object -First $cp | ForEach-Object { [Math]::Abs($_.Gap) } | Measure-Object -Sum).Sum, 2)
        $projected = [Math]::Round($ourTotal + $cumGain, 2)
        $md += "| #1–$cp | +$cumGain | ~$projected pts |`n"
    }

    # Tasks at parity or leading
    $md += "`n## Tasks at parity or leading (no action needed)`n`n"
    $md += "| Task | Folder | Status |`n"
    $md += "|---|---|---|`n"
    $okRows = $rows | Where-Object { $_.Gap -ge 0 -and -not ($_.Gap -eq 0 -and $_.Us -eq 0 -and $_.Leader -eq 0) }
    foreach ($r in $okRows) {
        $detail = if ($r.Gap -gt 0) { "(+$($r.Gap))" } else { "" }
        $md += "| $($r.Id) — $($r.Variant) | [$($r.Folder)]($($r.Folder)/) | $($r.Status) $detail |`n"
    }

    # Zero-score tasks
    $zeroRows = $rows | Where-Object { $_.Us -eq 0 -and $_.Leader -eq 0 }
    if ($zeroRows.Count -gt 0) {
        $md += "`n## Zero-score tasks (both teams)`n`n"
        $md += "| Task | Folder | Notes |`n"
        $md += "|---|---|---|`n"
        foreach ($r in $zeroRows) {
            $md += "| $($r.Id) — $($r.Variant) | [$($r.Folder)]($($r.Folder)/) | Both score 0 |`n"
        }
    }

    # All tasks reference table
    $md += "`n## All Tasks`n`n"
    $md += "| Task | Type | Variant | Tier | Us | Leader | Gap | Status | Folder |`n"
    $md += "|:---:|---|---|:---:|:---:|:---:|---:|---|---|`n"
    foreach ($r in $rows) {
        $md += "| $($r.Id) | ``$($r.Type)`` | $($r.Variant) | $($r.Tier) | $($r.Us) | $($r.Leader) | $($r.Gap) | $($r.Status) | [$($r.Folder)]($($r.Folder)/) |`n"
    }

    # Tier summary
    $t1 = $rows | Where-Object { $_.Tier -eq 1 }
    $t2 = $rows | Where-Object { $_.Tier -eq 2 }
    $t3 = $rows | Where-Object { $_.Tier -eq 3 }
    $md += "`n## Tier Summary`n`n"
    $md += "| Tier | Tasks | Our Total | Leader Total | Gap |`n"
    $md += "|---|:---:|:---:|:---:|---:|`n"
    foreach ($tier in @(
            @{ Name = "Tier 1 (basic CRUD)"; Rows = $t1; Range = "01-05" },
            @{ Name = "Tier 2 (multi-step)"; Rows = $t2; Range = "06-18" },
            @{ Name = "Tier 3 (advanced/PDF)"; Rows = $t3; Range = "19-30" }
        )) {
        $usSum = [Math]::Round(($tier.Rows | Measure-Object -Property Us -Sum).Sum, 2)
        $ldSum = [Math]::Round(($tier.Rows | Measure-Object -Property Leader -Sum).Sum, 2)
        $gapSum = [Math]::Round($usSum - $ldSum, 2)
        $md += "| $($tier.Name) | $($tier.Range) | $usSum | $ldSum | $gapSum |`n"
    }

    $prioFile = Join-Path $tasksDir "PRIORITY_EXECUTION_ORDER.md"
    Write-IfChanged -Path $prioFile -Content $md
    Write-Host "  PRIORITY_EXECUTION_ORDER.md — processed"
}
else {
    Write-Host "  PRIORITY_EXECUTION_ORDER.md — skipped (no leaderboard data)"
}

Write-Host "`nProcessed $generated task(s): $($script:updatedFiles) file(s) updated, $($script:skippedFiles) unchanged."
