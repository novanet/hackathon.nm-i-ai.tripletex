# Probe payroll-related API endpoints to diagnose competition check failures
# Run after a payroll test to investigate payslip discoverability

param(
    [int]$EmployeeId = 18618269,
    [int]$PayslipId = 32628360,
    [int]$TransactionId = 6957342,
    [int]$VoucherId = 608895034
)

$ErrorActionPreference = "Continue"

# Load credentials from user-secrets
Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*",""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*",""

$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json" }

function ApiGet($path) {
    try {
        $r = Invoke-RestMethod "$baseUrl$path" -Headers $headers -ErrorAction Stop
        return $r
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        Write-Host "  ERROR $status`: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

Write-Host "`n=== PROBE 1: Payslip search (unfiltered, count=100) ===" -ForegroundColor Cyan
$r = ApiGet "/salary/payslip?count=100"
if ($r) {
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values -and $r.values.Count -gt 0) {
        Write-Host "  First payslip:" -ForegroundColor Green
        $r.values[0] | ConvertTo-Json -Depth 4 | Write-Host
    } else {
        Write-Host "  No values returned" -ForegroundColor Yellow
    }
}

Write-Host "`n=== PROBE 2: Payslip search by employeeId ===" -ForegroundColor Cyan
$r = ApiGet "/salary/payslip?employeeId=$EmployeeId&count=100"
if ($r) {
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values -and $r.values.Count -gt 0) {
        Write-Host "  Found payslips for employee $EmployeeId" -ForegroundColor Green
        $r.values | ForEach-Object { Write-Host "    id=$($_.id) grossAmount=$($_.grossAmount) employee=$($_.employee.id)" }
    } else {
        Write-Host "  No payslips found for employee $EmployeeId" -ForegroundColor Yellow
    }
}

Write-Host "`n=== PROBE 3: Payslip search by date range ===" -ForegroundColor Cyan
$r = ApiGet "/salary/payslip?yearFrom=2026&monthFrom=1&yearTo=2026&monthTo=12&count=100"
if ($r) {
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values -and $r.values.Count -gt 0) {
        Write-Host "  Found payslips in date range" -ForegroundColor Green
        $r.values | ForEach-Object { Write-Host "    id=$($_.id) grossAmount=$($_.grossAmount) month=$($_.month) employee=$($_.employee.id)" }
    } else {
        Write-Host "  No payslips found in date range" -ForegroundColor Yellow
    }
}

Write-Host "`n=== PROBE 4: Payslip GET by known ID ===" -ForegroundColor Cyan
$r = ApiGet "/salary/payslip/$PayslipId`?fields=*"
if ($r) {
    Write-Host "  Payslip found:" -ForegroundColor Green
    $r.value | ConvertTo-Json -Depth 4 | Write-Host
}

Write-Host "`n=== PROBE 5: Salary compilation ===" -ForegroundColor Cyan
$r = ApiGet "/salary/compilation?employeeId=$EmployeeId&year=2026"
if ($r) {
    Write-Host "  Compilation response:" -ForegroundColor Green
    $r | ConvertTo-Json -Depth 4 | Write-Host
}

Write-Host "`n=== PROBE 6: Voucher GET (check employee on postings) ===" -ForegroundColor Cyan
$r = ApiGet "/ledger/voucher/$VoucherId`?fields=id,description,postings(*)"
if ($r) {
    Write-Host "  Voucher postings:" -ForegroundColor Green
    foreach ($p in $r.value.postings) {
        $empId = if ($p.employee) { $p.employee.id } else { "NULL" }
        Write-Host "    row=$($p.row) account=$($p.account.id) amount=$($p.amountGross) employee=$empId"
    }
}

Write-Host "`n=== PROBE 7: Transaction GET (check payslips) ===" -ForegroundColor Cyan
$r = ApiGet "/salary/transaction/$TransactionId`?fields=*"
if ($r) {
    Write-Host "  Transaction:" -ForegroundColor Green
    $r.value | ConvertTo-Json -Depth 4 | Write-Host
}

Write-Host "`n=== PROBE 8: Salary payslip search by wageTransactionId ===" -ForegroundColor Cyan
$r = ApiGet "/salary/payslip?wageTransactionId=$TransactionId&count=100"
if ($r) {
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values -and $r.values.Count -gt 0) {
        Write-Host "  Found payslips by transaction" -ForegroundColor Green
        $r.values | ForEach-Object { Write-Host "    id=$($_.id) grossAmount=$($_.grossAmount) employee=$($_.employee.id)" }
    } else {
        Write-Host "  No payslips found by transaction" -ForegroundColor Yellow
    }
}

Write-Host "`n=== DONE ===" -ForegroundColor Cyan
