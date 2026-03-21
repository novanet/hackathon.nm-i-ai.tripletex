<#
.SYNOPSIS
    Inspect entities created by the fixed-price project handler
#>
param([long]$ProjectId = 401997750, [long]$InvoiceId = 2147580048)

$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $SessionToken = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $SessionToken = $Matches[1].Trim() }
}
$authBytes = [System.Text.Encoding]::UTF8.GetBytes("0:$SessionToken")
$authHeader = "Basic " + [Convert]::ToBase64String($authBytes)
$headers = @{ "Authorization" = $authHeader; "Content-Type" = "application/json" }

function Invoke-Tx {
    param([string]$Path)
    $resp = Invoke-RestMethod -Uri "$BaseUrl$Path" -Headers $headers
    return $resp
}

Write-Host "=== Project $ProjectId ===" -ForegroundColor Cyan
$proj = Invoke-Tx "/project/$ProjectId`?fields=id,name,isFixedPrice,fixedprice,preliminaryInvoice,invoicingPlan,customer,projectManager,vatType"
Write-Host "name: $($proj.value.name)"
Write-Host "isFixedPrice: $($proj.value.isFixedPrice)"
Write-Host "fixedprice: $($proj.value.fixedprice)"
Write-Host "preliminaryInvoice: $($proj.value.preliminaryInvoice | ConvertTo-Json -Compress)"
Write-Host "invoicingPlan: $($proj.value.invoicingPlan | ConvertTo-Json -Compress)"
Write-Host "vatType: $($proj.value.vatType | ConvertTo-Json -Compress)"
Write-Host "customer.id: $($proj.value.customer.id)"
Write-Host "projectManager.id: $($proj.value.projectManager.id)"

Write-Host "`n=== Invoice $InvoiceId ===" -ForegroundColor Cyan
$inv = Invoke-Tx "/invoice/$InvoiceId`?fields=id,amount,amountExcludingVat,amountOutstanding,invoiceNumber"
Write-Host "id: $($inv.value.id)"
Write-Host "amount: $($inv.value.amount)"
Write-Host "amountExcludingVat: $($inv.value.amountExcludingVat)"
Write-Host "amountOutstanding: $($inv.value.amountOutstanding)"
Write-Host "invoiceNumber: $($inv.value.invoiceNumber)"

Write-Host "`n=== Orders linked to project ===" -ForegroundColor Cyan
$orders = Invoke-Tx "/order?projectId=$ProjectId&count=10&fields=id,orderNumber&orderDateFrom=2020-01-01&orderDateTo=2030-12-31"
foreach ($o in $orders.values) {
    Write-Host "Order $($o.id) (number: $($o.orderNumber))"
    $ol = Invoke-Tx "/order/orderline?orderId=$($o.id)&fields=id,description,unitPriceExcludingVatCurrency,unitPriceIncludingVatCurrency,amountExcludingVatCurrency,amountIncludingVatCurrency,vatType"
    foreach ($line in $ol.values) {
        Write-Host "  desc: $($line.description)"
        Write-Host "  unitPriceExcl: $($line.unitPriceExcludingVatCurrency)"
        Write-Host "  unitPriceIncl: $($line.unitPriceIncludingVatCurrency)"
        Write-Host "  amountExcl: $($line.amountExcludingVatCurrency)"
        Write-Host "  amountIncl: $($line.amountIncludingVatCurrency)"
        Write-Host "  vatType: $($line.vatType | ConvertTo-Json -Compress)"
    }
}

# Also check: what vatType does the PROJECT have?
Write-Host "`n=== Project vatType field ===" -ForegroundColor Cyan
$projFull = Invoke-Tx "/project/$ProjectId`?fields=vatType"
Write-Host "project.vatType: $($projFull.value.vatType | ConvertTo-Json -Compress)"

# Now create a test order WITH vatType to compare
Write-Host "`n=== PROBE: Order with explicit vatType (25% MVA) ===" -ForegroundColor Cyan
$vatResp = Invoke-Tx "/ledger/vatType?number=3&from=0&count=1&fields=id,number,percentage"
$vatId = $vatResp.values[0].id
$vatPct = $vatResp.values[0].percentage
Write-Host "VatType #3: id=$vatId, percentage=$vatPct"

$milestoneAmt = 119725  # 25% of 478900
$orderBody = @{
    customer     = @{ id = $proj.value.customer.id }
    project      = @{ id = $ProjectId }
    orderDate    = "2026-03-21"
    deliveryDate = "2026-03-21"
    orderLines   = @(
        @{
            description                   = "Probe: med VAT"
            count                         = 1
            unitPriceExcludingVatCurrency = $milestoneAmt
            vatType                       = @{ id = $vatId }
        }
    )
}
$orderJson = $orderBody | ConvertTo-Json -Depth 10 -Compress
$orderResp = Invoke-RestMethod -Method POST -Uri "$BaseUrl/order" -Headers $headers -Body $orderJson -ContentType "application/json"
$newOrderId = $orderResp.value.id
Write-Host "Created order $newOrderId with vatType #3"

$invBody = @{
    invoiceDate    = "2026-03-21"
    invoiceDueDate = "2026-04-21"
    orders         = @( @{ id = $newOrderId } )
}
$invJson = $invBody | ConvertTo-Json -Depth 10 -Compress
$invResp = Invoke-RestMethod -Method POST -Uri "$BaseUrl/invoice" -Headers $headers -Body $invJson -ContentType "application/json"
Write-Host "Invoice amount (with 25% VAT): $($invResp.value.amount)"
Write-Host "Invoice amountExcludingVat: $($invResp.value.amountExcludingVat)"
Write-Host "Expected: excl=$milestoneAmt, incl=$($milestoneAmt * 1.25)"
