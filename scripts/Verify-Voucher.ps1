Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*",""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*",""
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json"; "Content-Type" = "application/json" }

$voucherId = $args[0]
if (-not $voucherId) { $voucherId = "608895611" }

$r = Invoke-RestMethod "$baseUrl/ledger/voucher/$voucherId`?fields=id,number,description,voucherType(id,name),postings(id,row,account(number),amountGross,employee(id,firstName,lastName))" -Headers $headers
Write-Host "=== Voucher $voucherId ===" -ForegroundColor Cyan
Write-Host "  Type: $($r.value.voucherType.name)"
Write-Host "  Desc: $($r.value.description)"
foreach ($p in $r.value.postings) {
    $emp = if ($p.employee -and $p.employee.id) { "$($p.employee.firstName) $($p.employee.lastName) (id=$($p.employee.id))" } else { "NULL" }
    Write-Host "  Row $($p.row): acct=$($p.account.number) amt=$($p.amountGross) emp=$emp"
}
