# Probe voucher + posting search with proper required params
Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*", ""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*", ""
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json" }

function ApiGet($path) {
    try {
        $r = Invoke-RestMethod "$baseUrl$path" -Headers $headers -ErrorAction Stop
        return $r
    }
    catch {
        $body = $_.ErrorDetails.Message
        Write-Host "  ERROR: $body" -ForegroundColor Red
        return $null
    }
}

# Voucher search requires dateFrom/dateTo
Write-Host "=== Voucher search (dateFrom+dateTo) ===" -ForegroundColor Cyan
$r = ApiGet "/ledger/voucher?dateFrom=2026-03-01&dateTo=2026-03-31&count=10&fields=id,description,date,number"
if ($r) {
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) {
        foreach ($v in $r.values | Select-Object -Last 10) {
            Write-Host "  #$($v.number) id=$($v.id): '$($v.description)' ($($v.date))"
        }
    }
}

# Posting search requires dateFrom/dateTo
Write-Host "`n=== Posting search (dateFrom+dateTo, all postings) ===" -ForegroundColor Cyan
$r = ApiGet "/ledger/posting?dateFrom=2026-03-01&dateTo=2026-03-31&count=20&fields=id,date,description,account(id,number),amountGross,employee(id,firstName,lastName),voucher(id,description)"
if ($r) {
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) {
        foreach ($p in $r.values | Select-Object -Last 20) {
            $empName = if ($p.employee -and $p.employee.id) { "$($p.employee.firstName) $($p.employee.lastName) (id=$($p.employee.id))" } else { "none" }
            Write-Host "  posting $($p.id): acct=$($p.account.number) amt=$($p.amountGross) emp=$empName voucher=$($p.voucher.id)"
        }
    }
}

# Check our specific voucher with full posting details
Write-Host "`n=== Our voucher 608895034 (full postings with employee) ===" -ForegroundColor Cyan
$r = ApiGet "/ledger/voucher/608895034?fields=id,description,date,number,postings(id,date,description,account(id,number),amountGross,employee(id,firstName,lastName),row)"
if ($r) {
    Write-Host "  Voucher: #$($r.value.number) '$($r.value.description)' ($($r.value.date))"
    foreach ($p in $r.value.postings) {
        $empName = if ($p.employee -and $p.employee.id) { "$($p.employee.firstName) $($p.employee.lastName) (id=$($p.employee.id))" } else { "none" }
        Write-Host "  row=$($p.row) acct=$($p.account.number) amt=$($p.amountGross) emp=$empName desc='$($p.description)'"
    }
}

# Posting search by employeeId
Write-Host "`n=== Posting search by employeeId=18618269 ===" -ForegroundColor Cyan
$r = ApiGet "/ledger/posting?dateFrom=2026-01-01&dateTo=2026-12-31&employeeId=18618269&count=10&fields=id,date,description,account(number),amountGross,voucher(id,description)"
if ($r) {
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) {
        foreach ($p in $r.values) {
            Write-Host "  posting $($p.id): acct=$($p.account.number) amt=$($p.amountGross) voucher='$($p.voucher.description)'"
        }
    }
}
