# Probe: Does setting voucherType to "Lønnsbilag" change anything?
# Also test if payslip search depends on voucherType

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

$employeeId = 18618269
$lonnbilagTypeId = 9912094  # Lønnsbilag voucher type

# Get account IDs
$acct5000 = ApiGet "/ledger/account?number=5000&count=1&fields=id"
$acct1920 = ApiGet "/ledger/account?number=1920&count=1&fields=id"
$salaryAcctId = $acct5000.values[0].id
$bankAcctId = $acct1920.values[0].id

# ============================================================
Write-Host "=== 1. Create voucher WITH voucherType=Lønnsbilag ===" -ForegroundColor Cyan

$body1 = @{
    date = "2026-11-01"
    description = "Lønn Test November 2026"
    voucherType = @{ id = $lonnbilagTypeId }
    postings = @(
        @{
            date = "2026-11-01"
            description = "Lønn debit"
            account = @{ id = $salaryAcctId }
            amountGross = 40000
            amountGrossCurrency = 40000
            row = 1
            employee = @{ id = $employeeId }
        },
        @{
            date = "2026-11-01"
            description = "Lønn credit"
            account = @{ id = $bankAcctId }
            amountGross = -40000
            amountGrossCurrency = -40000
            row = 2
            employee = @{ id = $employeeId }
        }
    )
}

$result1 = ApiPost "/ledger/voucher?sendToLedger=true" $body1
if ($result1) {
    $vId1 = $result1.value.id
    Write-Host "  Created voucher $vId1 with voucherType=Lønnsbilag + emp on both rows" -ForegroundColor Green
    
    # Verify 
    $vDetail = ApiGet "/ledger/voucher/$vId1`?fields=id,number,description,voucherType(id,name),postings(id,row,account(number),amountGross,employee(id,firstName,lastName))"
    if ($vDetail) {
        $type = if ($vDetail.value.voucherType -and $vDetail.value.voucherType.name) { $vDetail.value.voucherType.name } else { "NULL" }
        Write-Host "  Voucher type: $type"
        foreach ($p in $vDetail.value.postings) {
            $emp = if ($p.employee -and $p.employee.id) { "$($p.employee.firstName) (id=$($p.employee.id))" } else { "NULL" }
            Write-Host "    row=$($p.row) acct=$($p.account.number) amt=$($p.amountGross) emp=$emp"
        }
    }
    
    # Check payslip search
    Start-Sleep -Seconds 2
    $psSearch = ApiGet "/salary/payslip?count=100"
    Write-Host "  Payslip search after Lønnsbilag voucher: count=$($psSearch.count)"
    
    # Check voucherDate-based payslip search
    $psVoucher = ApiGet "/salary/payslip?voucherDateFrom=2026-11-01&voucherDateTo=2026-11-02&count=100"
    Write-Host "  Payslip search by voucherDate: count=$(if ($psVoucher) { $psVoucher.count } else { 'ERROR' })"
    
    # Voucher search by type
    $vByType = ApiGet "/ledger/voucher?dateFrom=2026-11-01&dateTo=2026-11-02&count=100&fields=id,voucherType(id,name)"
    if ($vByType) {
        Write-Host "  Voucher search Nov: count=$($vByType.count)"
        foreach ($v in $vByType.values) {
            Write-Host "    id=$($v.id) type=$($v.voucherType.name)"
        }
    }
} else {
    Write-Host "  FAILED to create with Lønnsbilag type. Trying without type..." -ForegroundColor Red
}

# ============================================================
Write-Host "`n=== 2. Re-check existing voucher voucherType ===" -ForegroundColor Cyan
# Maybe the issue is that voucherType needs to be explicitly expanded in query
$existing = ApiGet "/ledger/voucher/608895034?fields=*"
if ($existing) {
    Write-Host "  Existing voucher 608895034 all fields:" -ForegroundColor Gray
    $existing.value | ConvertTo-Json -Depth 3 | Write-Host
}

# ============================================================
Write-Host "`n=== 3. Check if payslip has a voucher reference ===" -ForegroundColor Cyan
$ps = ApiGet "/salary/payslip/32628360?fields=*"
if ($ps) {
    Write-Host "  Payslip 32628360 full dump:" -ForegroundColor Gray
    $ps.value | ConvertTo-Json -Depth 3 | Write-Host
}

# ============================================================
Write-Host "`n=== 4. Try salary/payslip search with voucherDateFrom/To ===" -ForegroundColor Cyan
$r1 = ApiGet "/salary/payslip?voucherDateFrom=2026-01-01&voucherDateTo=2027-01-01&count=100"
Write-Host "  voucherDate range 2026: count=$(if ($r1) { $r1.count } else { 'ERROR' })"

$r2 = ApiGet "/salary/payslip?voucherDateFrom=2025-01-01&voucherDateTo=2027-01-01&count=100"
Write-Host "  voucherDate range 2025-2027: count=$(if ($r2) { $r2.count } else { 'ERROR' })"

Write-Host "`n=== DONE ===" -ForegroundColor Cyan
