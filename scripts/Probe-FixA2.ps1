# Probe Fix A with Ola Nordmann (confirmed working employment)
# Also dumps full transaction details to understand all fields

Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*",""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*",""
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json"; "Content-Type" = "application/json" }

function ApiGet($path) {
    try {
        return Invoke-RestMethod "$baseUrl$path" -Headers $headers -ErrorAction Stop
    } catch {
        $body = $_.ErrorDetails.Message
        Write-Host "  GET ERROR ($path):" -ForegroundColor Red
        Write-Host "    $body" -ForegroundColor DarkRed
        return $null
    }
}

function ApiPost($path, $body) {
    try {
        $json = $body | ConvertTo-Json -Depth 10
        return Invoke-RestMethod "$baseUrl$path" -Method Post -Headers $headers -Body $json -ErrorAction Stop
    } catch {
        $body2 = $_.ErrorDetails.Message
        Write-Host "  POST ERROR ($path):" -ForegroundColor Red
        Write-Host "    $body2" -ForegroundColor DarkRed
        return $null
    }
}

# Setup: use Ola Nordmann who has confirmed working employment
Write-Host "=== SETUP ===" -ForegroundColor Cyan
$empR = ApiGet "/employee?email=ola%40nordmann.no&count=1&fields=id,firstName,lastName"
if (-not $empR -or $empR.count -eq 0) {
    Write-Host "  Ola not found by email, trying by name..." -ForegroundColor Yellow
    $empR = ApiGet "/employee?firstName=Ola&lastName=Nordmann&count=1&fields=id,firstName,lastName"
}
if (-not $empR -or $empR.count -eq 0) {
    Write-Host "  Ola Nordmann not found. Trying hardcoded ID 18618269..." -ForegroundColor Yellow
    $employeeId = 18618269
} else {
    $employeeId = $empR.values[0].id
}
Write-Host "  Employee ID: $employeeId"

$stR = ApiGet "/salary/type?count=10&fields=id,number,name"
$baseSalaryTypeId = ($stR.values | Where-Object { $_.number -eq "2000" }).id
Write-Host "  Salary type 2000: id=$baseSalaryTypeId"

# First: dump the existing WORKING transaction from our handler run
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "=== EXISTING TRANSACTION (from handler) ===" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$existingTxId = 6957342  # From our previous test run
$tx = ApiGet "/salary/transaction/$existingTxId`?fields=*"
if ($tx) {
    Write-Host "  FULL TRANSACTION:" -ForegroundColor Green
    $tx.value | ConvertTo-Json -Depth 5 | Write-Host
} else {
    Write-Host "  Could not fetch existing transaction. Trying to find one..." -ForegroundColor Yellow
}

# Also dump existing payslip
$existingPsId = 32628360
$ps = ApiGet "/salary/payslip/$existingPsId`?fields=*"
if ($ps) {
    Write-Host "`n  FULL PAYSLIP:" -ForegroundColor Green
    $ps.value | ConvertTo-Json -Depth 5 | Write-Host
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== FIX A: isHistorical=true (no tax generation) ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$bodyA = @{
    date = "2026-08-01"
    year = 2026
    month = 8
    isHistorical = $true
    payslips = @(
        @{
            employee = @{ id = $employeeId }
            date = "2026-08-01"
            year = 2026
            month = 8
            specifications = @(
                @{
                    salaryType = @{ id = $baseSalaryTypeId }
                    rate = 30000
                    count = 1
                }
            )
        }
    )
}

# Without generateTaxDeduction param at all
$resultA = ApiPost "/salary/transaction" $bodyA
if ($resultA) {
    $txIdA = $resultA.value.id
    $psIdA = if ($resultA.value.payslips) { $resultA.value.payslips[0].id } else { "none" }
    Write-Host "  SUCCESS! Transaction $txIdA with isHistorical=true" -ForegroundColor Green
    Write-Host "  Payslip ID: $psIdA"
    Write-Host "  Full response:" -ForegroundColor Gray
    $resultA.value | ConvertTo-Json -Depth 5 | Write-Host
    
    Start-Sleep -Seconds 2
    
    Write-Host "`n  --- Payslip search after isHistorical=true ---" -ForegroundColor Yellow
    
    $r1 = ApiGet "/salary/payslip?count=100"
    Write-Host "    Unfiltered: count=$($r1.count)"
    if ($r1.count -gt 0) {
        Write-Host "    PAYSLIPS FOUND!" -ForegroundColor Green
        $r1.values | ForEach-Object { Write-Host "      id=$($_.id) employee=$($_.employee.id)" }
    }
    
    $r2 = ApiGet "/salary/payslip?employeeId=$employeeId&count=100"
    Write-Host "    By employee: count=$($r2.count)"
    
    # Full year range
    $r3 = ApiGet "/salary/payslip?yearFrom=2026&monthFrom=1&yearTo=2027&monthTo=1&count=100"
    Write-Host "    By full year: count=$(if ($r3) { $r3.count } else { 'ERROR' })"
    
    # Compilation
    $comp = ApiGet "/salary/compilation?employeeId=$employeeId&year=2026"
    if ($comp -and $comp.value) {
        $wageCount = if ($comp.value.wages) { $comp.value.wages.Count } else { 0 }
        Write-Host "    Compilation wages: $wageCount"
        if ($wageCount -gt 0) {
            Write-Host "    COMPILATION HAS DATA!" -ForegroundColor Green
            $comp.value.wages | ConvertTo-Json -Depth 3 | Write-Host
        }
    }
} else {
    Write-Host "  FAILED. Trying with generateTaxDeduction=false..." -ForegroundColor Red
    
    $resultA2 = ApiPost "/salary/transaction?generateTaxDeduction=false" $bodyA
    if ($resultA2) {
        $txIdA2 = $resultA2.value.id
        $psIdA2 = if ($resultA2.value.payslips) { $resultA2.value.payslips[0].id } else { "none" }
        Write-Host "  SUCCESS with generateTaxDeduction=false! Transaction $txIdA2" -ForegroundColor Green
        Write-Host "  Payslip ID: $psIdA2"
        Write-Host "  Full response:" -ForegroundColor Gray
        $resultA2.value | ConvertTo-Json -Depth 5 | Write-Host
        
        Start-Sleep -Seconds 2
        
        $r1 = ApiGet "/salary/payslip?count=100"
        Write-Host "    Unfiltered payslip search: count=$($r1.count)"
        if ($r1.count -gt 0) {
            Write-Host "    PAYSLIPS FOUND!" -ForegroundColor Green
        }
    } else {
        Write-Host "  Both attempts failed for isHistorical." -ForegroundColor Red
    }
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== EXISTING: All transactions via direct GET ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

# Can we list transactions? 
Write-Host "  Listing salary transactions..." -ForegroundColor Gray
$txList = ApiGet "/salary/transaction?count=20&fields=id,date,year,month,isHistorical,paySlipsAvailableDate"
if ($txList -and $txList.values) {
    Write-Host "  Found $($txList.count) transactions:" -ForegroundColor Green
    foreach ($t in $txList.values) {
        Write-Host "    tx=$($t.id) date=$($t.date) yr=$($t.year) mo=$($t.month) hist=$($t.isHistorical) psAvail=$($t.paySlipsAvailableDate)"
    }
}

Write-Host "`n=== DONE ===" -ForegroundColor Cyan
