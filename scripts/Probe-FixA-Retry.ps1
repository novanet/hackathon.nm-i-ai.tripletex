# Probe Fix A retry: isHistorical=true with generateTaxDeduction=false
# Also tests payslip search with corrected month range (monthTo is exclusive)

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
        Write-Host "  GET ERROR ($path): $body" -ForegroundColor Red
        return $null
    }
}

function ApiPost($path, $body) {
    try {
        $json = $body | ConvertTo-Json -Depth 10
        return Invoke-RestMethod "$baseUrl$path" -Method Post -Headers $headers -Body $json -ErrorAction Stop
    } catch {
        $body2 = $_.ErrorDetails.Message
        Write-Host "  POST ERROR ($path): $body2" -ForegroundColor Red
        return $null
    }
}

# Setup
Write-Host "=== SETUP ===" -ForegroundColor Cyan
$empR = ApiGet "/employee?count=1&fields=id,firstName,lastName"
$employeeId = $empR.values[0].id
Write-Host "  Employee: $($empR.values[0].firstName) $($empR.values[0].lastName) (id=$employeeId)"

$stR = ApiGet "/salary/type?count=10&fields=id,number,name"
$baseSalaryTypeId = ($stR.values | Where-Object { $_.number -eq "2000" }).id
Write-Host "  Salary type 2000: id=$baseSalaryTypeId"

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== FIX A (retry): isHistorical=true, NO generateTaxDeduction ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$bodyA2 = @{
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

# Try WITHOUT generateTaxDeduction param entirely
$resultA2 = ApiPost "/salary/transaction" $bodyA2
if ($resultA2) {
    $txIdA2 = $resultA2.value.id
    $psIdA2 = if ($resultA2.value.payslips) { $resultA2.value.payslips[0].id } else { "none" }
    Write-Host "  Created transaction $txIdA2 with isHistorical=true (no tax gen)" -ForegroundColor Green
    Write-Host "  Payslip ID: $psIdA2"
    Write-Host "  isHistorical in response: $($resultA2.value.isHistorical)"
    
    Start-Sleep -Seconds 2
    
    # Payslip search — try various patterns
    Write-Host "`n  Payslip search patterns:" -ForegroundColor Gray
    
    $r1 = ApiGet "/salary/payslip?count=100"
    Write-Host "    Unfiltered: count=$($r1.count)"
    
    $r2 = ApiGet "/salary/payslip?employeeId=$employeeId&count=100"
    Write-Host "    By employee: count=$($r2.count)"
    
    # Use monthTo = month+1 (since the range is exclusive)
    $r3 = ApiGet "/salary/payslip?yearFrom=2026&monthFrom=8&yearTo=2026&monthTo=9&count=100"
    Write-Host "    By date (8 to 9): count=$(if ($r3) { $r3.count } else { 'ERROR' })"
    
    $r4 = ApiGet "/salary/payslip?yearFrom=2026&monthFrom=1&yearTo=2027&monthTo=1&count=100"
    Write-Host "    By date (2026 full year): count=$(if ($r4) { $r4.count } else { 'ERROR' })"
    
    # Compilation check
    $comp = ApiGet "/salary/compilation?employeeId=$employeeId&year=2026"
    if ($comp -and $comp.value) {
        $wageCount = if ($comp.value.wages) { $comp.value.wages.Count } else { 0 }
        Write-Host "    Compilation wages: $wageCount"
    }
    
    # Direct payslip GET
    if ($psIdA2 -ne "none") {
        $ps = ApiGet "/salary/payslip/$psIdA2`?fields=*"
        if ($ps) {
            Write-Host "    Direct GET payslip: employee=$($ps.value.employee.id), grossAmount=$($ps.value.grossAmount)" -ForegroundColor Green
            # Dump full fields to see if there's something else useful
            Write-Host "    Full payslip response:" -ForegroundColor Gray
            $ps.value | ConvertTo-Json -Depth 5 | Write-Host
        }
    }
} else {
    Write-Host "  FAILED" -ForegroundColor Red
}

# Also try with generateTaxDeduction=false explicitly
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== FIX A (retry2): isHistorical=true, generateTaxDeduction=false ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$bodyA3 = @{
    date = "2026-09-01"
    year = 2026
    month = 9
    isHistorical = $true
    payslips = @(
        @{
            employee = @{ id = $employeeId }
            date = "2026-09-01"
            year = 2026
            month = 9
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

$resultA3 = ApiPost "/salary/transaction?generateTaxDeduction=false" $bodyA3
if ($resultA3) {
    $txIdA3 = $resultA3.value.id
    $psIdA3 = if ($resultA3.value.payslips) { $resultA3.value.payslips[0].id } else { "none" }
    Write-Host "  Created transaction $txIdA3 with isHistorical=true + genTaxDed=false" -ForegroundColor Green
    Write-Host "  Payslip ID: $psIdA3"
    Write-Host "  isHistorical in response: $($resultA3.value.isHistorical)"

    Start-Sleep -Seconds 2

    $r1 = ApiGet "/salary/payslip?count=100"
    Write-Host "  Unfiltered payslip search: count=$($r1.count)"
    
    $r2 = ApiGet "/salary/payslip?employeeId=$employeeId&count=100"
    Write-Host "  By employee: count=$($r2.count)"
    
    $r3 = ApiGet "/salary/payslip?yearFrom=2026&monthFrom=9&yearTo=2026&monthTo=10&count=100"
    Write-Host "  By date (9 to 10): count=$(if ($r3) { $r3.count } else { 'ERROR' })"
    
    # Check the full transaction response 
    $txDetail = ApiGet "/salary/transaction/$txIdA3`?fields=*"
    if ($txDetail) {
        Write-Host "  Transaction fields:" -ForegroundColor Gray
        Write-Host "    hasVoucher: $($txDetail.value.hasVoucher)"
        Write-Host "    year: $($txDetail.value.year), month: $($txDetail.value.month)"
        Write-Host "    isHistorical: $($txDetail.value.isHistorical)"
    }
} else {
    Write-Host "  FAILED" -ForegroundColor Red
}

# ============================================================
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "=== BROADER: Check salary/settings and salary/transaction endpoints ===" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

$settings = ApiGet "/salary/settings?fields=*"
if ($settings) {
    Write-Host "  Salary settings:" -ForegroundColor Gray
    $settings.value | ConvertTo-Json -Depth 3 | Write-Host
}

# Check if there's a way to "close" or "complete" a salary period
Write-Host "`n  Trying various completion endpoints..." -ForegroundColor Gray

$rClose = ApiGet "/salary/transaction/$txIdA2`?fields=*" 2>$null
if ($rClose) {
    Write-Host "  Transaction detail for isHistorical=true tx:" -ForegroundColor Gray
    Write-Host "    id: $($rClose.value.id)"
    Write-Host "    date: $($rClose.value.date)"
    Write-Host "    year: $($rClose.value.year), month: $($rClose.value.month)"
    Write-Host "    isHistorical: $($rClose.value.isHistorical)"
    Write-Host "    hasVoucher: $($rClose.value.hasVoucher)"
    Write-Host "    paySlipsAvailableDate: $($rClose.value.paySlipsAvailableDate)"
    # See all fields
    Write-Host "    ALL FIELDS:" -ForegroundColor Cyan
    $rClose.value | ConvertTo-Json -Depth 3 | Write-Host
}

Write-Host "`n=== DONE ===" -ForegroundColor Cyan
