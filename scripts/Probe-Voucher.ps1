# Probe voucher search + posting search
Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*",""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*",""
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json" }

# Voucher search without typeId
Write-Host "=== Voucher search (no filters, last 5) ===" -ForegroundColor Cyan
try {
    $r = Invoke-RestMethod "$baseUrl/ledger/voucher?count=5&fields=id,description,date,number&sorting=-date" -Headers $headers
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) {
        foreach ($v in $r.values) {
            Write-Host "  #$($v.number) id=$($v.id): '$($v.description)' ($($v.date))"
        }
    }
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }

# Posting search - can we find postings with employee?
Write-Host "`n=== Posting search (by account 5000, recent) ===" -ForegroundColor Cyan
try {
    $r = Invoke-RestMethod "$baseUrl/ledger/posting?count=10&dateFrom=2026-01-01&fields=id,date,description,account(id,number),amountGross,employee(id,firstName,lastName),voucher(id)" -Headers $headers
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) {
        foreach ($p in $r.values | Select-Object -Last 10) {
            $empName = if ($p.employee) { "$($p.employee.firstName) $($p.employee.lastName) (id=$($p.employee.id))" } else { "NULL" }
            Write-Host "  posting $($p.id): account=$($p.account.number) amount=$($p.amountGross) employee=$empName voucher=$($p.voucher.id)"
        }
    }
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }

# Posting search specifically for employee
Write-Host "`n=== Posting search (by employeeId) ===" -ForegroundColor Cyan
try {
    $r = Invoke-RestMethod "$baseUrl/ledger/posting?count=10&employeeId=18618269&fields=id,date,description,account(id,number),amountGross,employee(id,firstName,lastName),voucher(id)" -Headers $headers
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) {
        foreach ($p in $r.values) {
            Write-Host "  posting $($p.id): account=$($p.account.number) amount=$($p.amountGross) employee=$($p.employee.firstName) $($p.employee.lastName)"
        }
    } else { Write-Host "  No postings found" -ForegroundColor Yellow }
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }

# Try to see if 5000-account postings have employee  
Write-Host "`n=== Posting search (account 5000, dateFrom 2026-03-01) ===" -ForegroundColor Cyan
try {
    $acct = Invoke-RestMethod "$baseUrl/ledger/account?number=5000&count=1&fields=id" -Headers $headers
    $acctId = $acct.values[0].id
    $r = Invoke-RestMethod "$baseUrl/ledger/posting?count=10&accountId=$acctId&dateFrom=2026-03-01&fields=id,date,description,amountGross,employee(id,firstName,lastName),voucher(id,description)" -Headers $headers
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) {
        foreach ($p in $r.values) {
            $empName = if ($p.employee) { "$($p.employee.firstName) $($p.employee.lastName) (id=$($p.employee.id))" } else { "NULL" }
            Write-Host "  posting $($p.id): amount=$($p.amountGross) employee=$empName voucher='$($p.voucher.description)'"
        }
    }
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }
