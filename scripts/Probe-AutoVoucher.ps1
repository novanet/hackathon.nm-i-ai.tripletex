# Probe: Do salary transactions auto-create vouchers?
# And does the competition maybe find payroll data via voucher search?

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
        Write-Host "  GET ERROR ($path): $($_.ErrorDetails.Message)" -ForegroundColor Red
        return $null
    }
}

function ApiPost($path, $body) {
    try {
        $json = $body | ConvertTo-Json -Depth 10
        return Invoke-RestMethod "$baseUrl$path" -Method Post -Headers $headers -Body $json -ErrorAction Stop
    } catch {
        Write-Host "  POST ERROR ($path): $($_.ErrorDetails.Message)" -ForegroundColor Red
        return $null
    }
}

$employeeId = 18618269  # Ola Nordmann
$stR = ApiGet "/salary/type?count=10&fields=id,number,name"
$baseSalaryTypeId = ($stR.values | Where-Object { $_.number -eq "2000" }).id

# ============================================================
Write-Host "=== 1. Check if our March salary transaction (6957342) created a voucher ===" -ForegroundColor Cyan
Write-Host "Search for all vouchers on 2026-03-01 (salary transaction date)" -ForegroundColor Gray

$vMarch = ApiGet "/ledger/voucher?dateFrom=2026-03-01&dateTo=2026-03-02&count=100&fields=id,number,date,description,voucherType(id,name),postings(id,row,account(number),amountGross,employee(id))"
if ($vMarch) {
    Write-Host "  Vouchers on 2026-03-01: count=$($vMarch.count)" -ForegroundColor Green
    foreach ($v in $vMarch.values) {
        $type = if ($v.voucherType) { $v.voucherType.name } else { "?" }
        Write-Host "  Voucher id=$($v.id) num=$($v.number) type=$type desc='$($v.description)'"
        foreach ($p in $v.postings) {
            $emp = if ($p.employee -and $p.employee.id) { $p.employee.id } else { "null" }
            Write-Host "    row=$($p.row) acct=$($p.account.number) amt=$($p.amountGross) emp=$emp"
        }
    }
}

# ============================================================
Write-Host "`n=== 2. Search ALL voucher types ===" -ForegroundColor Cyan
$vtypes = ApiGet "/ledger/voucherType?count=100&fields=id,name"
if ($vtypes) {
    Write-Host "  Voucher types:" -ForegroundColor Gray
    foreach ($vt in $vtypes.values) {
        Write-Host "    id=$($vt.id) name=$($vt.name)"
    }
}

# ============================================================  
Write-Host "`n=== 3. Search vouchers for August (where isHistorical=true tx was) ===" -ForegroundColor Cyan
$vAug = ApiGet "/ledger/voucher?dateFrom=2026-08-01&dateTo=2026-08-02&count=100&fields=id,number,date,description,voucherType(id,name),postings(id,row,account(number),amountGross,employee(id))"
if ($vAug) {
    Write-Host "  Vouchers on 2026-08-01: count=$($vAug.count)" -ForegroundColor Green
    foreach ($v in $vAug.values) {
        $type = if ($v.voucherType) { $v.voucherType.name } else { "?" }
        Write-Host "  Voucher id=$($v.id) num=$($v.number) type=$type desc='$($v.description)'"
        foreach ($p in $v.postings) {
            $emp = if ($p.employee -and $p.employee.id) { $p.employee.id } else { "null" }
            Write-Host "    row=$($p.row) acct=$($p.account.number) amt=$($p.amountGross) emp=$emp"
        }
    }
}

# ============================================================
Write-Host "`n=== 4. Create a NEW salary transaction on a clean date (Oct) and check for auto-voucher ===" -ForegroundColor Cyan

$bodyNew = @{
    date = "2026-10-01"
    year = 2026
    month = 10
    payslips = @(
        @{
            employee = @{ id = $employeeId }
            date = "2026-10-01"
            year = 2026
            month = 10
            specifications = @(
                @{
                    salaryType = @{ id = $baseSalaryTypeId }
                    rate = 25000
                    count = 1
                }
            )
        }
    )
}

$resultNew = ApiPost "/salary/transaction?generateTaxDeduction=true" $bodyNew
if ($resultNew) {
    $txIdNew = $resultNew.value.id
    $psIdNew = if ($resultNew.value.payslips) { $resultNew.value.payslips[0].id } else { "none" }
    Write-Host "  New transaction: id=$txIdNew, payslip=$psIdNew"
    
    Start-Sleep -Seconds 2
    
    Write-Host "  Searching for vouchers on 2026-10-01..." -ForegroundColor Gray
    $vOct = ApiGet "/ledger/voucher?dateFrom=2026-10-01&dateTo=2026-10-02&count=100&fields=id,number,date,description,voucherType(id,name),postings(id,row,account(number,name),amountGross,employee(id))"
    if ($vOct) {
        Write-Host "  Vouchers on 2026-10-01: count=$($vOct.count)" -ForegroundColor $(if ($vOct.count -gt 0) { "Green" } else { "Red" })
        foreach ($v in $vOct.values) {
            $type = if ($v.voucherType) { $v.voucherType.name } else { "?" }
            Write-Host "  Voucher id=$($v.id) num=$($v.number) type=$type desc='$($v.description)'"
            foreach ($p in $v.postings) {
                $emp = if ($p.employee -and $p.employee.id) { $p.employee.id } else { "null" }
                Write-Host "    row=$($p.row) acct=$($p.account.number)($($p.account.name)) amt=$($p.amountGross) emp=$emp"
            }
        }
    }
    
    # Also check postings for this date
    Write-Host "  Searching for postings on 2026-10-01..." -ForegroundColor Gray
    $pOct = ApiGet "/ledger/posting?dateFrom=2026-10-01&dateTo=2026-10-02&count=100&fields=id,account(number,name),amountGross,employee(id),voucher(id,number)"
    if ($pOct) {
        Write-Host "  Postings on 2026-10-01: count=$($pOct.count)" -ForegroundColor $(if ($pOct.count -gt 0) { "Green" } else { "Red" })
        foreach ($p in $pOct.values) {
            $emp = if ($p.employee -and $p.employee.id) { $p.employee.id } else { "null" }
            Write-Host "    acct=$($p.account.number)($($p.account.name)) amt=$($p.amountGross) emp=$emp voucher=$($p.voucher.id)/#$($p.voucher.number)"
        }
    }
}

# ============================================================
Write-Host "`n=== 5. Check our manual voucher vs salary-auto voucher ===" -ForegroundColor Cyan
# Our handler's manual voucher was 608895034 on the March run
$manualV = ApiGet "/ledger/voucher/608895034?fields=id,number,description,voucherType(id,name),postings(id,row,account(number),amountGross,employee(id))"
if ($manualV) {
    $type = if ($manualV.value.voucherType) { $manualV.value.voucherType.name } else { "?" }
    Write-Host "  Manual voucher 608895034: type=$type desc='$($manualV.value.description)'"
    foreach ($p in $manualV.value.postings) {
        $emp = if ($p.employee -and $p.employee.id) { $p.employee.id } else { "null" }
        Write-Host "    row=$($p.row) acct=$($p.account.number) amt=$($p.amountGross) emp=$emp"
    }
}

# ============================================================
Write-Host "`n=== 6. Try wageTransactionId filter on payslip search ===" -ForegroundColor Cyan
# The wageTransactionId might be different from transaction ID
$r = ApiGet "/salary/payslip?wageTransactionId=$txIdNew&count=100"
Write-Host "  wageTransactionId=$txIdNew : count=$(if ($r) { $r.count } else { 'ERROR' })"

$r = ApiGet "/salary/payslip?wageTransactionId=6957342&count=100"
Write-Host "  wageTransactionId=6957342 (existing): count=$(if ($r) { $r.count } else { 'ERROR' })"

Write-Host "`n=== DONE ===" -ForegroundColor Cyan
