# Test-VoucherToSupplierInvoice.ps1 — Does a voucher with supplier ref appear in /supplierInvoice?
$ErrorActionPreference = "Stop"

$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $SessionToken = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $SessionToken = $Matches[1].Trim() }
}

$pair = "0:$SessionToken"
$bytes = [System.Text.Encoding]::ASCII.GetBytes($pair)
$b64 = [Convert]::ToBase64String($bytes)

$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $b64)
$client.Timeout = [TimeSpan]::FromSeconds(30)

function Do-Get([string]$path) {
    $uri = "$BaseUrl$path"
    Write-Host "  GET $uri"
    $task = $client.GetAsync($uri)
    $task.Wait()
    $resp = $task.Result
    $ct = $resp.Content.ReadAsStringAsync()
    $ct.Wait()
    Write-Host "  Status: $([int]$resp.StatusCode) $($resp.StatusCode)"
    $body = $ct.Result
    if ($body.Length -gt 800) { Write-Host "  Body: $($body.Substring(0, 800))..." } else { Write-Host "  Body: $body" }
    return @{ Status = [int]$resp.StatusCode; Body = $body }
}

function Do-Post([string]$path, [string]$jsonBody) {
    $uri = "$BaseUrl$path"
    Write-Host "  POST $uri"
    Write-Host "  Payload: $jsonBody"
    $content = [System.Net.Http.StringContent]::new($jsonBody, [System.Text.Encoding]::UTF8, "application/json")
    $task = $client.PostAsync($uri, $content)
    $task.Wait()
    $resp = $task.Result
    $ct = $resp.Content.ReadAsStringAsync()
    $ct.Wait()
    Write-Host "  Status: $([int]$resp.StatusCode) $($resp.StatusCode)"
    $body = $ct.Result
    if ($body.Length -gt 800) { Write-Host "  Body: $($body.Substring(0, 800))..." } else { Write-Host "  Body: $body" }
    return @{ Status = [int]$resp.StatusCode; Body = $body }
}

$today = (Get-Date).ToString("yyyy-MM-dd")
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")

# Step 1: Create a test supplier
Write-Host "=== Step 1: Create supplier ==="
$r1 = Do-Post "/supplier" '{"name":"VoucherTest-Supplier","organizationNumber":"998877661"}'
$supplierId = $null
if ($r1.Status -eq 201) {
    $parsed = $r1.Body | ConvertFrom-Json
    $supplierId = $parsed.value.id
    Write-Host "  Supplier ID: $supplierId"
}
if (-not $supplierId) {
    Write-Host "ERROR: No supplier created. Stopping."
    $client.Dispose()
    exit 1
}

# Step 2: Resolve accounts
Write-Host "`n=== Step 2: Resolve accounts ==="
$r2a = Do-Get ("/ledger/account?number=6500" + "&from=0&count=1&fields=id,number,vatType(id),vatLocked")
$acctId = $null
if ($r2a.Status -eq 200) {
    $parsed = $r2a.Body | ConvertFrom-Json
    if ($parsed.values.Count -gt 0) {
        $acctId = $parsed.values[0].id
        Write-Host "  Account 6500 id=$acctId vatLocked=$($parsed.values[0].vatLocked)"
    }
}

$r2b = Do-Get ("/ledger/account?number=2400" + "&from=0&count=1&fields=id")
$creditorId = $null
if ($r2b.Status -eq 200) {
    $parsed = $r2b.Body | ConvertFrom-Json
    if ($parsed.values.Count -gt 0) { $creditorId = $parsed.values[0].id }
}

# Step 3: Get VAT type (25% input)
Write-Host "`n=== Step 3: Resolve VAT type ==="
$r3 = Do-Get ("/ledger/vatType?number=1" + "&from=0&count=1&fields=id")
$vatId = $null
if ($r3.Status -eq 200) {
    $parsed = $r3.Body | ConvertFrom-Json
    if ($parsed.values.Count -gt 0) { $vatId = $parsed.values[0].id; Write-Host "  VatType id=$vatId" }
}

# Step 4: Get voucher type
Write-Host "`n=== Step 4: Resolve voucher type ==="
$r4 = Do-Get ("/ledger/voucherType?name=Leverand%C3%B8rfaktura" + "&count=5&fields=id,name")
$voucherTypeId = $null
if ($r4.Status -eq 200) {
    $parsed = $r4.Body | ConvertFrom-Json
    if ($parsed.values.Count -gt 0) {
        $voucherTypeId = $parsed.values[0].id
        Write-Host "  VoucherType 'Leverandorfaktura' id=$voucherTypeId"
    } else {
        Write-Host "  WARNING: No voucherType found for 'Leverandorfaktura'!"
    }
}

# Step 5: Create voucher with supplier ref (same as our handler does)
Write-Host "`n=== Step 5: POST /ledger/voucher with supplier ref ==="
$voucherBody = @{
    date = $today
    description = "Test supplier invoice voucher"
    vendorInvoiceNumber = "TEST-VERIFY-002"
    postings = @(
        @{
            date = $today
            description = "Test supplier invoice"
            account = @{ id = $acctId }
            amountGross = 10000
            amountGrossCurrency = 10000
            supplier = @{ id = $supplierId }
            row = 1
        }
        @{
            date = $today
            description = "Test supplier invoice"
            account = @{ id = $creditorId }
            amountGross = -10000
            amountGrossCurrency = -10000
            supplier = @{ id = $supplierId }
            row = 2
        }
    )
}
if ($voucherTypeId) { $voucherBody["voucherType"] = @{ id = $voucherTypeId } }
if ($vatId) { $voucherBody.postings[0]["vatType"] = @{ id = $vatId } }

$jsonPayload = $voucherBody | ConvertTo-Json -Depth 5
$r5 = Do-Post "/ledger/voucher?sendToLedger=true" $jsonPayload

$voucherId = $null
if ($r5.Status -eq 201 -or $r5.Status -eq 200) {
    $parsed = $r5.Body | ConvertFrom-Json
    $voucherId = $parsed.value.id
    Write-Host "  Created voucher ID: $voucherId"
} else {
    Write-Host "  ERROR: Voucher creation failed!"
    $client.Dispose()
    exit 1
}

# Step 6: THE KEY TEST - Does it appear in /supplierInvoice?
Write-Host "`n=== Step 6: CRITICAL TEST - Search /supplierInvoice ==="
$r6a = Do-Get ("/supplierInvoice?invoiceDateFrom=$yesterday" + "&invoiceDateTo=$tomorrow" + "&from=0&count=10")
if ($r6a.Status -eq 200) {
    $parsed = $r6a.Body | ConvertFrom-Json
    Write-Host "`n  *** SUPPLIER INVOICES FOUND: $($parsed.values.Count) ***"
    foreach ($si in $parsed.values) {
        Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
    }
}

# Also search by voucherId
Write-Host "`n=== Step 6b: Search /supplierInvoice by voucherId ==="
$r6b = Do-Get ("/supplierInvoice?invoiceDateFrom=$yesterday" + "&invoiceDateTo=$tomorrow" + "&voucherId=$voucherId" + "&from=0&count=5")
if ($r6b.Status -eq 200) {
    $parsed = $r6b.Body | ConvertFrom-Json
    Write-Host "`n  *** SUPPLIER INVOICES MATCHING VOUCHER: $($parsed.values.Count) ***"
}

# Also search by supplier
Write-Host "`n=== Step 6c: Search /supplierInvoice by supplierId ==="
$r6c = Do-Get ("/supplierInvoice?invoiceDateFrom=$yesterday" + "&invoiceDateTo=$tomorrow" + "&supplierId=$supplierId" + "&from=0&count=5")
if ($r6c.Status -eq 200) {
    $parsed = $r6c.Body | ConvertFrom-Json
    Write-Host "`n  *** SUPPLIER INVOICES MATCHING SUPPLIER: $($parsed.values.Count) ***"
}

# Step 7: Also try GET /supplierInvoice/{voucherId} directly
Write-Host "`n=== Step 7: GET /supplierInvoice/{voucherId} directly ==="
$r7 = Do-Get "/supplierInvoice/$voucherId"

# Step 8: Also test POST /incomingInvoice (maybe it works for POST but not search?)
Write-Host "`n=== Step 8: POST /incomingInvoice (direct test) ==="
$dueDateStr = (Get-Date).AddDays(30).ToString("yyyy-MM-dd")
$iiBody = @{
    invoiceHeader = @{
        vendorId = $supplierId
        invoiceDate = $today
        dueDate = $dueDateStr
        invoiceAmount = 5000
        description = "Test incoming invoice"
        invoiceNumber = "TEST-VERIFY-003"
    }
    orderLines = @(
        @{
            row = 1
            accountId = $acctId
            amountInclVat = 5000
            description = "Test line"
        }
    )
} | ConvertTo-Json -Depth 4

$r8 = Do-Post "/incomingInvoice" $iiBody

$client.Dispose()
Write-Host "`n========================================="
Write-Host "VERIFICATION COMPLETE"
Write-Host "========================================="
