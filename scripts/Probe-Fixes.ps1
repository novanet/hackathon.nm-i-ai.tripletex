# Probe candidate fixes A-E by creating transactions directly via API
# Tests whether different parameters make payslips searchable

Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*", ""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*", ""
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json"; "Content-Type" = "application/json" }

function ApiGet($path) {
    try {
        return Invoke-RestMethod "$baseUrl$path" -Headers $headers -ErrorAction Stop
    }
    catch {
        $body = $_.ErrorDetails.Message
        Write-Host "  GET ERROR: $body" -ForegroundColor Red
        return $null
    }
}

function ApiPost($path, $body) {
    try {
        $json = $body | ConvertTo-Json -Depth 10
        return Invoke-RestMethod "$baseUrl$path" -Method Post -Headers $headers -Body $json -ErrorAction Stop
    }
    catch {
        $body2 = $_.ErrorDetails.Message
        Write-Host "  POST ERROR: $body2" -ForegroundColor Red
        return $null
    }
}

function CheckPayslipSearch($label, $employeeId, $year, $month) {
    Write-Host "  Checking payslip search after $label..." -ForegroundColor Gray
    
    # Search unfiltered
    $r = ApiGet "/salary/payslip?count=100"
    $unfilteredCount = if ($r) { $r.count } else { "ERROR" }
    
    # Search by employee
    $r = ApiGet "/salary/payslip?employeeId=$employeeId&count=100"
    $byEmpCount = if ($r) { $r.count } else { "ERROR" }
    
    # Search by date
    $r = ApiGet "/salary/payslip?yearFrom=$year&monthFrom=$month&yearTo=$year&monthTo=$month&count=100"
    $byDateCount = if ($r) { $r.count } else { "ERROR" }
    
    Write-Host "    Unfiltered: $unfilteredCount | ByEmployee: $byEmpCount | ByDate: $byDateCount"
    return @{ Unfiltered = $unfilteredCount; ByEmployee = $byEmpCount; ByDate = $byDateCount }
}

function CheckCompilation($employeeId, $year) {
    $r = ApiGet "/salary/compilation?employeeId=$employeeId&year=$year"
    if ($r -and $r.value) {
        $wageCount = if ($r.value.wages) { $r.value.wages.Count } else { 0 }
        $expCount = if ($r.value.expenses) { $r.value.expenses.Count } else { 0 }
        $taxCount = if ($r.value.taxDeductions) { $r.value.taxDeductions.Count } else { 0 }
        Write-Host "    Compilation: wages=$wageCount, expenses=$expCount, taxDeductions=$taxCount, vacationPayBasis=$($r.value.vacationPayBasis)"
    }
}

# Setup: find employee with employment + salary type
Write-Host "=== SETUP ===" -ForegroundColor Cyan
$empR = ApiGet "/employee?count=1&fields=id,firstName,lastName&email=ola%40nordmann.no"
if (-not $empR -or $empR.count -eq 0) {
    # Use any existing employee
    $empR = ApiGet "/employee?count=1&fields=id,firstName,lastName"
}
$employeeId = $empR.values[0].id
Write-Host "  Employee: $($empR.values[0].firstName) $($empR.values[0].lastName) (id=$employeeId)"

$stR = ApiGet "/salary/type?count=10&fields=id,number,name"
$baseSalaryTypeId = ($stR.values | Where-Object { $_.number -eq "2000" }).id
Write-Host "  Salary type 2000: id=$baseSalaryTypeId"

# Verify employment exists
$emplR = ApiGet "/employee/employment?employeeId=$employeeId&count=1&fields=id"
Write-Host "  Employment: count=$($emplR.count)"

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== FIX A: isHistorical=true ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$bodyA = @{
    date         = "2026-04-01"
    year         = 2026
    month        = 4
    isHistorical = $true
    payslips     = @(
        @{
            employee       = @{ id = $employeeId }
            date           = "2026-04-01"
            year           = 2026
            month          = 4
            specifications = @(
                @{
                    salaryType = @{ id = $baseSalaryTypeId }
                    rate       = 30000
                    count      = 1
                }
            )
        }
    )
}

$resultA = ApiPost "/salary/transaction?generateTaxDeduction=true" $bodyA
if ($resultA) {
    $txIdA = $resultA.value.id
    $psIdA = if ($resultA.value.payslips) { $resultA.value.payslips[0].id } else { "none" }
    Write-Host "  Created transaction $txIdA with isHistorical=true" -ForegroundColor Green
    Write-Host "  Payslip ID: $psIdA"
    Write-Host "  isHistorical in response: $($resultA.value.isHistorical)"
    
    # Now check if payslip is searchable
    Start-Sleep -Seconds 2  # Give API time to index
    $searchA = CheckPayslipSearch "Fix A (isHistorical=true)" $employeeId 2026 4
    CheckCompilation $employeeId 2026
    
    # Also verify payslip GET works
    if ($psIdA -ne "none") {
        $psDetail = ApiGet "/salary/payslip/$psIdA`?fields=id,employee(id),grossAmount"
        if ($psDetail) {
            Write-Host "    Payslip GET: employee=$($psDetail.value.employee.id), grossAmount=$($psDetail.value.grossAmount)" -ForegroundColor Green
        }
    }
}
else {
    Write-Host "  FAILED to create transaction with isHistorical=true" -ForegroundColor Red
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== FIX B: paySlipsAvailableDate in past ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$bodyB = @{
    date                  = "2026-05-01"
    year                  = 2026
    month                 = 5
    paySlipsAvailableDate = "2026-01-01"
    payslips              = @(
        @{
            employee       = @{ id = $employeeId }
            date           = "2026-05-01"
            year           = 2026
            month          = 5
            specifications = @(
                @{
                    salaryType = @{ id = $baseSalaryTypeId }
                    rate       = 30000
                    count      = 1
                }
            )
        }
    )
}

$resultB = ApiPost "/salary/transaction?generateTaxDeduction=true" $bodyB
if ($resultB) {
    $txIdB = $resultB.value.id
    $psIdB = if ($resultB.value.payslips) { $resultB.value.payslips[0].id } else { "none" }
    Write-Host "  Created transaction $txIdB with paySlipsAvailableDate=2026-01-01" -ForegroundColor Green
    Write-Host "  Payslip ID: $psIdB"
    Write-Host "  paySlipsAvailableDate in response: $($resultB.value.paySlipsAvailableDate)"
    
    Start-Sleep -Seconds 2
    $searchB = CheckPayslipSearch "Fix B (paySlipsAvailableDate)" $employeeId 2026 5
    CheckCompilation $employeeId 2026
    
    if ($psIdB -ne "none") {
        $psDetail = ApiGet "/salary/payslip/$psIdB`?fields=id,employee(id),grossAmount"
        if ($psDetail) {
            Write-Host "    Payslip GET: employee=$($psDetail.value.employee.id), grossAmount=$($psDetail.value.grossAmount)" -ForegroundColor Green
        }
    }
}
else {
    Write-Host "  FAILED to create transaction with paySlipsAvailableDate" -ForegroundColor Red
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== FIX A+B COMBINED: isHistorical + paySlipsAvailableDate ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$bodyAB = @{
    date                  = "2026-06-01"
    year                  = 2026
    month                 = 6
    isHistorical          = $true
    paySlipsAvailableDate = "2026-01-01"
    payslips              = @(
        @{
            employee       = @{ id = $employeeId }
            date           = "2026-06-01"
            year           = 2026
            month          = 6
            specifications = @(
                @{
                    salaryType = @{ id = $baseSalaryTypeId }
                    rate       = 30000
                    count      = 1
                }
            )
        }
    )
}

$resultAB = ApiPost "/salary/transaction?generateTaxDeduction=true" $bodyAB
if ($resultAB) {
    $txIdAB = $resultAB.value.id
    $psIdAB = if ($resultAB.value.payslips) { $resultAB.value.payslips[0].id } else { "none" }
    Write-Host "  Created transaction $txIdAB with both flags" -ForegroundColor Green
    Write-Host "  Payslip ID: $psIdAB"
    Write-Host "  isHistorical: $($resultAB.value.isHistorical), paySlipsAvailableDate: $($resultAB.value.paySlipsAvailableDate)"
    
    Start-Sleep -Seconds 2
    $searchAB = CheckPayslipSearch "Fix A+B combined" $employeeId 2026 6
    CheckCompilation $employeeId 2026
}
else {
    Write-Host "  FAILED to create combined transaction" -ForegroundColor Red
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== FIX D: Voucher with employee on BOTH posting rows ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

# Get account IDs
$acct5000 = ApiGet "/ledger/account?number=5000&count=1&fields=id"
$acct1920 = ApiGet "/ledger/account?number=1920&count=1&fields=id"
$salaryAcctId = $acct5000.values[0].id
$bankAcctId = $acct1920.values[0].id

$bodyD = @{
    date        = "2026-03-15"
    description = "Lønn test - employee on both rows"
    postings    = @(
        @{
            date                = "2026-03-15"
            description         = "Lønn debit"
            account             = @{ id = $salaryAcctId }
            amountGross         = 30000
            amountGrossCurrency = 30000
            row                 = 1
            employee            = @{ id = $employeeId }
        },
        @{
            date                = "2026-03-15"
            description         = "Lønn credit"
            account             = @{ id = $bankAcctId }
            amountGross         = -30000
            amountGrossCurrency = -30000
            row                 = 2
            employee            = @{ id = $employeeId }
        }
    )
}

$resultD = ApiPost "/ledger/voucher?sendToLedger=true" $bodyD
if ($resultD) {
    $vIdD = $resultD.value.id
    Write-Host "  Created voucher $vIdD with employee on both rows" -ForegroundColor Green
    
    # Verify both postings have employee
    $vDetail = ApiGet "/ledger/voucher/$vIdD`?fields=id,postings(id,row,account(number),amountGross,employee(id,firstName,lastName))"
    if ($vDetail) {
        foreach ($p in $vDetail.value.postings) {
            $empName = if ($p.employee -and $p.employee.id) { "$($p.employee.firstName) $($p.employee.lastName) (id=$($p.employee.id))" } else { "NULL" }
            Write-Host "    row=$($p.row) acct=$($p.account.number) amt=$($p.amountGross) employee=$empName"
        }
    }
    
    # Check posting search by employee - do we now get 2 results?
    $postings = ApiGet "/ledger/posting?dateFrom=2026-03-15&dateTo=2026-03-15&employeeId=$employeeId&count=100&fields=id,account(number),amountGross"
    if ($postings) {
        Write-Host "    Posting search by employee on 2026-03-15: count=$($postings.count)"
    }
}
else {
    Write-Host "  FAILED to create voucher with employee on both rows" -ForegroundColor Red
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta  
Write-Host "=== FIX E: Try generateTaxDeduction=false ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$bodyE = @{
    date     = "2026-07-01"
    year     = 2026
    month    = 7
    payslips = @(
        @{
            employee       = @{ id = $employeeId }
            date           = "2026-07-01"
            year           = 2026
            month          = 7
            specifications = @(
                @{
                    salaryType = @{ id = $baseSalaryTypeId }
                    rate       = 30000
                    count      = 1
                }
            )
        }
    )
}

$resultE = ApiPost "/salary/transaction?generateTaxDeduction=false" $bodyE
if ($resultE) {
    $txIdE = $resultE.value.id
    $psIdE = if ($resultE.value.payslips) { $resultE.value.payslips[0].id } else { "none" }
    Write-Host "  Created transaction $txIdE WITHOUT generateTaxDeduction" -ForegroundColor Green
    Write-Host "  Payslip ID: $psIdE"
    
    Start-Sleep -Seconds 2
    $searchE = CheckPayslipSearch "Fix E (no tax deduction)" $employeeId 2026 7
    CheckCompilation $employeeId 2026
}
else {
    Write-Host "  FAILED" -ForegroundColor Red
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== SUMMARY ===" -ForegroundColor Magenta
Write-Host "========================================`n" -ForegroundColor Magenta

# Final comprehensive check: are ANY payslips searchable now?
Write-Host "Final unfiltered payslip search:" -ForegroundColor Cyan
$final = ApiGet "/salary/payslip?count=1000"
if ($final) {
    Write-Host "  Total payslips found via search: $($final.count)" -ForegroundColor $(if ($final.count -gt 0) { "Green" } else { "Red" })
}

Write-Host "`nFinal compilation check:" -ForegroundColor Cyan 
CheckCompilation $employeeId 2026

Write-Host "`n=== DONE ===" -ForegroundColor Cyan
