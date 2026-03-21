<#
.SYNOPSIS
    Probe sandbox API to test fixed-price project hypotheses for Task 15.
.DESCRIPTION
    Tests:
    1.1 POST fixed-price project, GET back, verify fields
    1.2 PUT with partial fields → does isFixedPrice reset?
    1.3 PUT with ALL fields → preserved?
    1.4 Order+Invoice VAT semantics (no vatType on project-linked order)
    1.5 GET fields=* vs explicit field list
#>

$ErrorActionPreference = "Stop"

# Load credentials from user-secrets
$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $SessionToken = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $SessionToken = $Matches[1].Trim() }
}
if (-not $BaseUrl -or -not $SessionToken) { Write-Error "Missing credentials"; exit 1 }

$authBytes = [System.Text.Encoding]::UTF8.GetBytes("0:$SessionToken")
$authHeader = "Basic " + [Convert]::ToBase64String($authBytes)
$headers = @{ "Authorization" = $authHeader; "Content-Type" = "application/json" }

function Invoke-Tx {
    param([string]$Method, [string]$Path, $Body)
    $uri = "$BaseUrl$Path"
    $params = @{ Method = $Method; Uri = $uri; Headers = $headers; ContentType = "application/json" }
    if ($Body) { $params.Body = ($Body | ConvertTo-Json -Depth 10 -Compress) }
    try {
        $resp = Invoke-RestMethod @params
        return $resp
    }
    catch {
        $err = $_.ErrorDetails.Message
        Write-Host "  ERROR ($Method $Path): $err" -ForegroundColor Red
        return $null
    }
}

Write-Host "`n========== PROBE 1.1: POST fixed-price project + GET ==========" -ForegroundColor Cyan

# First get/create a customer for the project
$custResp = Invoke-Tx -Method GET -Path "/customer?name=ProbeFixedPriceCust&from=0&count=1"
if ($custResp.values -and $custResp.values.Count -gt 0) {
    $custId = $custResp.values[0].id
    Write-Host "  Using existing customer ID: $custId"
}
else {
    $custBody = @{ name = "ProbeFixedPriceCust"; email = "probe@test.no" }
    $custResp = Invoke-Tx -Method POST -Path "/customer" -Body $custBody
    $custId = $custResp.value.id
    Write-Host "  Created customer ID: $custId"
}

# Get any existing employee for project manager
$empResp = Invoke-Tx -Method GET -Path "/employee?from=0&count=1"
if ($empResp.values -and $empResp.values.Count -gt 0) {
    $pmId = $empResp.values[0].id
    Write-Host "  Using existing PM employee ID: $pmId"
}
else {
    Write-Error "No employees found in sandbox - cannot continue"
    exit 1
}

# POST a fixed-price project
$projBody = @{
    name           = "ProbeFixedPrice_$(Get-Date -Format 'HHmmss')"
    isFixedPrice   = $true
    fixedprice     = 100000
    startDate      = "2026-03-21"
    customer       = @{ id = $custId }
    projectManager = @{ id = $pmId }
    isInternal     = $false
}

Write-Host "`n  POST /project with isFixedPrice=true, fixedprice=100000"
$projResp = Invoke-Tx -Method POST -Path "/project" -Body $projBody
if (-not $projResp) { Write-Error "Failed to create project"; exit 1 }
$projId = $projResp.value.id
$projVersion = $projResp.value.version
Write-Host "  Created project ID: $projId, version: $projVersion"
Write-Host "  POST response isFixedPrice: $($projResp.value.isFixedPrice)"
Write-Host "  POST response fixedprice: $($projResp.value.fixedprice)"

# GET it back with specific fields
$getResp = Invoke-Tx -Method GET -Path "/project/$projId`?fields=id,version,name,isFixedPrice,fixedprice,customer,projectManager,startDate,endDate,isInternal,description,number"
Write-Host "`n  GET /project/$projId (explicit fields):"
Write-Host "    id: $($getResp.value.id)"
Write-Host "    version: $($getResp.value.version)"
Write-Host "    name: $($getResp.value.name)"
Write-Host "    isFixedPrice: $($getResp.value.isFixedPrice)"
Write-Host "    fixedprice: $($getResp.value.fixedprice)"
Write-Host "    startDate: $($getResp.value.startDate)"
Write-Host "    endDate: $($getResp.value.endDate)"
Write-Host "    isInternal: $($getResp.value.isInternal)"
Write-Host "    description: $($getResp.value.description)"
Write-Host "    number: $($getResp.value.number)"
Write-Host "    customer.id: $($getResp.value.customer.id)"
Write-Host "    projectManager.id: $($getResp.value.projectManager.id)"

$getVersion = $getResp.value.version

Write-Host "`n========== PROBE 1.2: PUT with PARTIAL fields (simulate bug) ==========" -ForegroundColor Cyan

# Simulate the current buggy PUT: only a few fields
$partialPut = @{
    id        = $projId
    version   = $getVersion
    name      = $getResp.value.name
    startDate = $getResp.value.startDate
    # DELIBERATELY OMITTING: isFixedPrice, fixedprice, endDate, isInternal, description, customer, projectManager
}

Write-Host "  PUT /project/$projId with ONLY id, version, name, startDate"
$putResp = Invoke-Tx -Method PUT -Path "/project/$projId" -Body $partialPut

if ($putResp) {
    Write-Host "  PUT response:"
    Write-Host "    isFixedPrice: $($putResp.value.isFixedPrice)"
    Write-Host "    fixedprice: $($putResp.value.fixedprice)"
    Write-Host "    customer.id: $($putResp.value.customer.id)" 
    Write-Host "    projectManager.id: $($putResp.value.projectManager.id)"
    Write-Host "    isInternal: $($putResp.value.isInternal)"
    Write-Host "    startDate: $($putResp.value.startDate)"
    Write-Host "    endDate: $($putResp.value.endDate)"
    $putVersion = $putResp.value.version

    # Verify with GET
    $verifyResp = Invoke-Tx -Method GET -Path "/project/$projId`?fields=id,version,name,isFixedPrice,fixedprice,customer,projectManager,startDate,endDate,isInternal"
    Write-Host "`n  GET after partial PUT:"
    Write-Host "    isFixedPrice: $($verifyResp.value.isFixedPrice) $(if ($verifyResp.value.isFixedPrice -eq $false) { '<-- RESET! H1 CONFIRMED' } else { '<-- preserved' })"
    Write-Host "    fixedprice: $($verifyResp.value.fixedprice) $(if ($verifyResp.value.fixedprice -eq 0) { '<-- RESET! H1 CONFIRMED' } else { '<-- preserved' })"
    Write-Host "    customer.id: $($verifyResp.value.customer.id)"
    Write-Host "    projectManager.id: $($verifyResp.value.projectManager.id)"
}
else {
    Write-Host "  PUT with partial fields FAILED (4xx error)" -ForegroundColor Yellow
    Write-Host "  This means Tripletex REQUIRES certain fields — the PUT may not silently reset them"
    
    # Re-fetch to get latest version for next probe
    $verifyResp = Invoke-Tx -Method GET -Path "/project/$projId`?fields=id,version,isFixedPrice,fixedprice"
    Write-Host "  Project still has isFixedPrice=$($verifyResp.value.isFixedPrice), fixedprice=$($verifyResp.value.fixedprice)"
}

Write-Host "`n========== PROBE 1.3: PUT with ALL fields (correct approach) ==========" -ForegroundColor Cyan

# Re-fetch the full project
$fullGet = Invoke-Tx -Method GET -Path "/project/$projId`?fields=id,version,name,number,description,startDate,endDate,isFixedPrice,fixedprice,isInternal,customer,projectManager,department"

# Build PUT body from ALL fields
$fullPut = @{
    id           = $fullGet.value.id
    version      = $fullGet.value.version
    name         = $fullGet.value.name
    startDate    = $fullGet.value.startDate
    isFixedPrice = $fullGet.value.isFixedPrice
    fixedprice   = $fullGet.value.fixedprice
    isInternal   = $fullGet.value.isInternal
}
if ($fullGet.value.endDate) { $fullPut.endDate = $fullGet.value.endDate }
if ($fullGet.value.description) { $fullPut.description = $fullGet.value.description }
if ($fullGet.value.number) { $fullPut.number = $fullGet.value.number }
if ($fullGet.value.customer) { $fullPut.customer = @{ id = $fullGet.value.customer.id } }
if ($fullGet.value.projectManager) { $fullPut.projectManager = @{ id = $fullGet.value.projectManager.id } }
if ($fullGet.value.department) { $fullPut.department = @{ id = $fullGet.value.department.id } }

Write-Host "  PUT /project/$projId with ALL fields"
$fullPutResp = Invoke-Tx -Method PUT -Path "/project/$projId" -Body $fullPut

if ($fullPutResp) {
    Write-Host "  PUT response:"
    Write-Host "    isFixedPrice: $($fullPutResp.value.isFixedPrice) $(if ($fullPutResp.value.isFixedPrice) { '<-- preserved OK' } else { '<-- LOST!' })"
    Write-Host "    fixedprice: $($fullPutResp.value.fixedprice) $(if ($fullPutResp.value.fixedprice -gt 0) { '<-- preserved OK' } else { '<-- LOST!' })"
    Write-Host "    customer.id: $($fullPutResp.value.customer.id)"
    Write-Host "    projectManager.id: $($fullPutResp.value.projectManager.id)"
}

Write-Host "`n========== PROBE 1.4: Order+Invoice VAT semantics ==========" -ForegroundColor Cyan

$milestoneAmount = 25000  # 25% of 100000

# Create order linked to project, NO vatType
$orderBody = @{
    customer     = @{ id = $custId }
    orderDate    = "2026-03-21"
    deliveryDate = "2026-03-21"
    orderLines   = @(
        @{
            description                   = "Milestone payment 25%"
            count                         = 1
            unitPriceExcludingVatCurrency = $milestoneAmount
        }
    )
}

Write-Host "  POST /order (linked to project, NO vatType, unitPriceExcl=$milestoneAmount)"
$orderResp = Invoke-Tx -Method POST -Path "/order" -Body $orderBody
if (-not $orderResp) { 
    Write-Host "  Order without vatType FAILED - trying with vatType" -ForegroundColor Yellow
    
    # Try with vatType
    $vatResp = Invoke-Tx -Method GET -Path "/ledger/vatType?number=3&from=0&count=1"
    $vatId = $vatResp.values[0].id
    Write-Host "  Got vatType ID $vatId for code 3 (25%)"
    
    $orderBody.orderLines[0].vatType = @{ id = $vatId }
    $orderResp = Invoke-Tx -Method POST -Path "/order" -Body $orderBody
}

if ($orderResp) {
    $orderId = $orderResp.value.id
    Write-Host "  Created order ID: $orderId"
    
    # Read order lines to check VAT
    $orderLinesResp = Invoke-Tx -Method GET -Path "/order/orderline?orderId=$orderId&from=0&count=10"
    foreach ($ol in $orderLinesResp.values) {
        Write-Host "    OrderLine: unitPriceExcl=$($ol.unitPriceExcludingVatCurrency), unitPriceIncl=$($ol.unitPriceIncludingVatCurrency)"
        Write-Host "      vatType: $($ol.vatType | ConvertTo-Json -Compress)" 
        Write-Host "      amountExcl=$($ol.amountExcludingVatCurrency), amountIncl=$($ol.amountIncludingVatCurrency)"
    }
    
    # Create invoice from order
    Write-Host "`n  POST /invoice (from order $orderId)"
    $invoiceBody = @{
        invoiceDate    = "2026-03-21"
        invoiceDueDate = "2026-04-21"
        orders         = @( @{ id = $orderId } )
    }
    $invResp = Invoke-Tx -Method POST -Path "/invoice" -Body $invoiceBody
    if ($invResp) {
        $invId = $invResp.value.id
        Write-Host "  Created invoice ID: $invId"
        Write-Host "    amount: $($invResp.value.amount)"
        Write-Host "    amountExcludingVat: $($invResp.value.amountExcludingVat)"
        Write-Host "    amountOutstanding: $($invResp.value.amountOutstanding)"
        
        $expectedInclVat = $milestoneAmount * 1.25
        Write-Host "`n  Analysis:"
        Write-Host "    unitPriceExcludingVat = $milestoneAmount"
        Write-Host "    expected if 25% VAT = $expectedInclVat"
        Write-Host "    expected if 0% VAT = $milestoneAmount"
        Write-Host "    actual invoice amount = $($invResp.value.amount)"
        if ($invResp.value.amount -eq $expectedInclVat) {
            Write-Host "    --> 25% VAT applied (standard rate)" -ForegroundColor Green
        }
        elseif ($invResp.value.amount -eq $milestoneAmount) {
            Write-Host "    --> 0% VAT (exempt/no VAT)" -ForegroundColor Yellow
        }
        else {
            Write-Host "    --> UNEXPECTED amount! Check VAT rate." -ForegroundColor Red
        }
    }
}

Write-Host "`n========== PROBE 1.5: GET fields=* vs explicit ==========" -ForegroundColor Cyan

$starResp = Invoke-Tx -Method GET -Path "/project/$projId`?fields=*"
$explResp = Invoke-Tx -Method GET -Path "/project/$projId`?fields=id,version,name,isFixedPrice,fixedprice,preliminaryInvoice,invoicingPlan"

Write-Host "  fields=* response keys: $( ($starResp.value | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name) -join ', ' )"
Write-Host "  fields=* has preliminaryInvoice: $($null -ne $starResp.value.preliminaryInvoice)"
Write-Host "  fields=* preliminaryInvoice value: $($starResp.value.preliminaryInvoice | ConvertTo-Json -Compress)"
Write-Host "  Explicit fields has preliminaryInvoice: $($null -ne $explResp.value.preliminaryInvoice)"
Write-Host "  Explicit fields preliminaryInvoice value: $($explResp.value.preliminaryInvoice | ConvertTo-Json -Compress)"
Write-Host "  fields=* has invoicingPlan: $($null -ne $starResp.value.invoicingPlan)"

# Check if isFixedPrice survived all our PUTs
Write-Host "`n  Final project state:"
Write-Host "    isFixedPrice: $($starResp.value.isFixedPrice)"
Write-Host "    fixedprice: $($starResp.value.fixedprice)"

Write-Host "`n========== ALL PROBES COMPLETE ==========" -ForegroundColor Green
