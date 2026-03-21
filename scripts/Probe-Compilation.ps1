# Quick salary compilation deep probe
param([int]$EmployeeId = 18618269)

Push-Location "$PSScriptRoot\..\src"
$secrets = dotnet user-secrets list 2>$null
Pop-Location
$token = ($secrets | Where-Object { $_ -match "^Tripletex:SessionToken" }) -replace "^[^=]+=\s*", ""
$baseUrl = ($secrets | Where-Object { $_ -match "^Tripletex:BaseUrl" }) -replace "^[^=]+=\s*", ""
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$token"))
$headers = @{ Authorization = "Basic $cred"; Accept = "application/json" }

# Compilation with all fields
Write-Host "=== Compilation (fields=*) ===" -ForegroundColor Cyan
$r = Invoke-RestMethod "$baseUrl/salary/compilation?employeeId=$EmployeeId&year=2026&fields=*" -Headers $headers
$r.value | ConvertTo-Json -Depth 8 | Write-Host

# Also try salary settings
Write-Host "`n=== Salary Settings ===" -ForegroundColor Cyan
$r = Invoke-RestMethod "$baseUrl/salary/settings?fields=*" -Headers $headers
$r.value | ConvertTo-Json -Depth 4 | Write-Host

# Try payslip search with different filter combos
Write-Host "`n=== Payslip search: yearFrom only ===" -ForegroundColor Cyan
$r = Invoke-RestMethod "$baseUrl/salary/payslip?yearFrom=2025&count=100" -Headers $headers
Write-Host "  count=$($r.count)"

Write-Host "`n=== Payslip search: id filter ===" -ForegroundColor Cyan
$r = Invoke-RestMethod "$baseUrl/salary/payslip?id=32628360" -Headers $headers
Write-Host "  count=$($r.count), values=$(if($r.values){$r.values.Count}else{0})"
if ($r.values -and $r.values.Count -gt 0) {
    $r.values[0] | ConvertTo-Json -Depth 4 | Write-Host
}

# Try existing payslips via employee - search for ALL employees
Write-Host "`n=== Employees with payroll ===" -ForegroundColor Cyan
$empR = Invoke-RestMethod "$baseUrl/employee?count=5&fields=id,firstName,lastName" -Headers $headers
foreach ($emp in $empR.values) {
    $ps = Invoke-RestMethod "$baseUrl/salary/payslip?employeeId=$($emp.id)&count=5" -Headers $headers
    Write-Host "  Employee $($emp.id) ($($emp.firstName) $($emp.lastName)): payslips=$($ps.count)"
}
