<#
.SYNOPSIS
    Deep investigation: preliminaryInvoice + orders + vatType behavior
#>
$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $SessionToken = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $SessionToken = $Matches[1].Trim() }
}
$authBytes = [System.Text.Encoding]::UTF8.GetBytes("0:$SessionToken")
$authHeader = "Basic " + [Convert]::ToBase64String($authBytes)
$headers = @{ "Authorization" = $authHeader; "Content-Type" = "application/json" }

function TX-Get([string]$Path) {
    return Invoke-RestMethod -Uri "$BaseUrl$Path" -Headers $headers
}
function TX-Post([string]$Path, $Body) {
    $json = $Body | ConvertTo-Json -Depth 10 -Compress
    return Invoke-RestMethod -Method POST -Uri "$BaseUrl$Path" -Headers $headers -Body $json -ContentType "application/json"
}
function TX-Put([string]$Path, $Body) {
    $json = $Body | ConvertTo-Json -Depth 10 -Compress
    return Invoke-RestMethod -Method PUT -Uri "$BaseUrl$Path" -Headers $headers -Body $json -ContentType "application/json"
}

$projId = 401997750

Write-Host "=== 1. Check orders linked to project ===" -ForegroundColor Cyan
try {
    $orders = TX-Get "/order?projectId=$projId&count=10&fields=id&orderDateFrom=2020-01-01&orderDateTo=2030-12-31"
    Write-Host "Orders found: $($orders.values.Count)"
    foreach ($o in $orders.values) { Write-Host "  Order ID: $($o.id)" }
}
catch { Write-Host "Order search failed: $_" -ForegroundColor Red }

Write-Host "`n=== 2. Check invoices linked to project ===" -ForegroundColor Cyan
try {
    $invs = TX-Get "/invoice?projectId=$projId&count=10&fields=id,amount&invoiceDateFrom=2020-01-01&invoiceDateTo=2030-12-31"
    Write-Host "Invoices found: $($invs.values.Count)"
    foreach ($i in $invs.values) { Write-Host "  Invoice ID: $($i.id), amount: $($i.amount)" }
}
catch { Write-Host "Invoice search failed: $_" -ForegroundColor Red }

Write-Host "`n=== 3. Read preliminaryInvoice directly ===" -ForegroundColor Cyan
$proj = TX-Get "/project/$projId`?fields=id,version,preliminaryInvoice"
Write-Host "preliminaryInvoice raw: $($proj.value.preliminaryInvoice | ConvertTo-Json -Compress -Depth 5)"

Write-Host "`n=== 4. Try setting preliminaryInvoice again (re-PUT) ===" -ForegroundColor Cyan
$invId = 2147580048
$projFull = TX-Get "/project/$projId`?fields=id,version,name,startDate,isFixedPrice,fixedprice,customer,projectManager,isInternal"
$putBody = @{
    id                 = $projId
    version            = $projFull.value.version
    name               = $projFull.value.name
    startDate          = $projFull.value.startDate
    isFixedPrice       = $projFull.value.isFixedPrice
    fixedprice         = $projFull.value.fixedprice
    isInternal         = $projFull.value.isInternal
    customer           = @{ id = $projFull.value.customer.id }
    projectManager     = @{ id = $projFull.value.projectManager.id }
    preliminaryInvoice = @{ id = $invId }
}
Write-Host "PUT body: $($putBody | ConvertTo-Json -Depth 5 -Compress)"
try {
    $putResult = TX-Put "/project/$projId" $putBody
    Write-Host "PUT succeeded!"
    Write-Host "After PUT - preliminaryInvoice: $($putResult.value.preliminaryInvoice | ConvertTo-Json -Compress)"
}
catch {
    Write-Host "PUT failed: $_" -ForegroundColor Red
}

# Re-read
$check = TX-Get "/project/$projId`?fields=id,preliminaryInvoice"
Write-Host "Re-read - preliminaryInvoice: $($check.value.preliminaryInvoice | ConvertTo-Json -Compress)"

Write-Host "`n=== 5. List valid vatTypes ===" -ForegroundColor Cyan
$vats = TX-Get "/ledger/vatType?from=0&count=30&fields=id,number,name,percentage"
foreach ($v in $vats.values) {
    Write-Host "  #$($v.number) (id=$($v.id)): $($v.name) ($($v.percentage)%)"
}

Write-Host "`n=== 6. Check what vatType the project has ===" -ForegroundColor Cyan
$projVat = TX-Get "/project/$projId`?fields=vatType"
Write-Host "Project vatType: #$($projVat.value.vatType.number) ($($projVat.value.vatType.name)), pct=$($projVat.value.vatType.percentage)"

Write-Host "`n=== 7. Can we change the project's vatType? ===" -ForegroundColor Cyan
$projUpdate = TX-Get "/project/$projId`?fields=id,version,name,startDate,isFixedPrice,fixedprice,customer,projectManager,isInternal"
$vatType3 = ($vats.values | Where-Object { $_.number -eq 3 }).id
Write-Host "Trying to set vatType #3 (id=$vatType3) on project..."
$putBody2 = @{
    id             = $projId
    version        = $projUpdate.value.version
    name           = $projUpdate.value.name
    startDate      = $projUpdate.value.startDate
    isFixedPrice   = $projUpdate.value.isFixedPrice
    fixedprice     = $projUpdate.value.fixedprice
    isInternal     = $projUpdate.value.isInternal
    customer       = @{ id = $projUpdate.value.customer.id }
    projectManager = @{ id = $projUpdate.value.projectManager.id }
    vatType        = @{ id = $vatType3 }
}
try {
    $putResult2 = TX-Put "/project/$projId" $putBody2
    Write-Host "PUT with vatType succeeded! New vatType: #$($putResult2.value.vatType.number)"
}
catch {
    Write-Host "PUT with vatType FAILED: $_" -ForegroundColor Red
}
