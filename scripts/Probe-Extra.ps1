# Probe for additional salary-related endpoints and action endpoints
Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*",""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*",""
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json" }

# Try salary transaction search (no documented endpoint, but try anyway)
Write-Host "=== Salary transaction search ===" -ForegroundColor Cyan
try {
    $r = Invoke-RestMethod "$baseUrl/salary/transaction?count=10" -Headers $headers
    Write-Host "  count=$($r.count)"
    if ($r.values) { $r.values | ForEach-Object { Write-Host "    id=$($_.id) date=$($_.date)" } }
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }

# Try salary specification search  
Write-Host "`n=== Salary specification search ===" -ForegroundColor Cyan
try {
    $r = Invoke-RestMethod "$baseUrl/salary/specification?count=10" -Headers $headers
    Write-Host "  count=$($r.count)"
    if ($r.values) { $r.values | ForEach-Object { Write-Host "    id=$($_.id) rate=$($_.rate)" } }
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }

# Try salary payment
Write-Host "`n=== Salary payment ===" -ForegroundColor Cyan
try {
    $r = Invoke-RestMethod "$baseUrl/salary/payment?count=10" -Headers $headers
    Write-Host "  count=$($r.count)"
    if ($r.values) { $r.values | ForEach-Object { Write-Host "    id=$($_.id)" } } 
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }

# Check voucher search - can we find our salary vouchers?
Write-Host "`n=== Voucher search (description contains 'Lønn') ===" -ForegroundColor Cyan
try {
    $r = Invoke-RestMethod "$baseUrl/ledger/voucher?count=5&fields=id,description,date,postings(id,account(id,number),employee(id,firstName,lastName),amountGross)&typeId=0" -Headers $headers
    Write-Host "  count=$($r.count), fullCount=$($r.fullCount)"
    if ($r.values) { 
        foreach ($v in $r.values | Select-Object -First 5) {
            Write-Host "  Voucher $($v.id): $($v.description) ($($v.date))" -ForegroundColor Green
            foreach ($p in $v.postings) {
                $empName = if ($p.employee) { "$($p.employee.firstName) $($p.employee.lastName) (id=$($p.employee.id))" } else { "NULL" }
                Write-Host "    posting: account=$($p.account.number) amount=$($p.amountGross) employee=$empName"
            }
        }
    }
} catch { Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red }

# Try creating a salary transaction with isHistorical=true to test if it changes payslip searchability
# (DON'T actually do this yet - just note it as a hypothesis)
Write-Host "`n=== Hypothesis: isHistorical might affect payslip searchability ===" -ForegroundColor Yellow
Write-Host "  Current transactions use isHistorical=false" -ForegroundColor Yellow
Write-Host "  Worth testing: does isHistorical=true make payslips searchable?" -ForegroundColor Yellow
