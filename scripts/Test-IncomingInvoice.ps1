
# Test-IncomingInvoice.ps1 — Verify /incomingInvoice endpoint accessibility
# This is a temporary diagnostic script for sandbox API verification.

$ErrorActionPreference = "Stop"

# Load credentials from user-secrets
$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $SessionToken = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $SessionToken = $Matches[1].Trim() }
}

if (-not $BaseUrl -or -not $SessionToken) {
    Write-Host "ERROR: Could not load credentials from user-secrets"
    exit 1
}

Write-Host "BaseUrl: $BaseUrl"
Write-Host "Token loaded: yes (length $($SessionToken.Length))"

# Setup HttpClient
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
    try {
        $task = $client.GetAsync($uri)
        $task.Wait()
        $resp = $task.Result
        $ct = $resp.Content.ReadAsStringAsync()
        $ct.Wait()
        $body = $ct.Result
        Write-Host "  Status: $([int]$resp.StatusCode) $($resp.StatusCode)"
        if ($body.Length -gt 800) {
            Write-Host "  Body (truncated): $($body.Substring(0, 800))..."
        }
        else {
            Write-Host "  Body: $body"
        }
        return @{ Status = [int]$resp.StatusCode; Body = $body }
    }
    catch {
        Write-Host "  EXCEPTION: $($_.Exception.Message)"
        return @{ Status = 0; Body = $_.Exception.Message }
    }
}

function Do-Post([string]$path, [string]$jsonBody) {
    $uri = "$BaseUrl$path"
    Write-Host "  POST $uri"
    Write-Host "  Request body: $jsonBody"
    try {
        $content = [System.Net.Http.StringContent]::new($jsonBody, [System.Text.Encoding]::UTF8, "application/json")
        $task = $client.PostAsync($uri, $content)
        $task.Wait()
        $resp = $task.Result
        $ct = $resp.Content.ReadAsStringAsync()
        $ct.Wait()
        $body = $ct.Result
        Write-Host "  Status: $([int]$resp.StatusCode) $($resp.StatusCode)"
        if ($body.Length -gt 800) {
            Write-Host "  Body (truncated): $($body.Substring(0, 800))..."
        }
        else {
            Write-Host "  Body: $body"
        }
        return @{ Status = [int]$resp.StatusCode; Body = $body }
    }
    catch {
        Write-Host "  EXCEPTION: $($_.Exception.Message)"
        return @{ Status = 0; Body = $_.Exception.Message }
    }
}

Write-Host ""
Write-Host "========================================="
Write-Host "R1: GET /incomingInvoice/search"
Write-Host "Purpose: Check if endpoint is accessible"
Write-Host "========================================="
$r1 = Do-Get ("/incomingInvoice/search?from=0" + "&count=5")

if ($r1.Status -eq 403 -or $r1.Status -eq 401) {
    Write-Host "`nR1 RESULT: BLOCKED ($($r1.Status)) — /incomingInvoice not accessible."
    Write-Host "`nProceeding to test /supplierInvoice and /ledger/voucher search instead..."
}

# R1b: GET /supplierInvoice — is this endpoint accessible?
Write-Host ""
Write-Host "========================================="
Write-Host "R1b: GET /supplierInvoice (search)"
Write-Host "Purpose: Check if /supplierInvoice search is accessible"
Write-Host "========================================="
$today = (Get-Date).ToString("yyyy-MM-dd")
$yearAgo = (Get-Date).AddYears(-1).ToString("yyyy-MM-dd")
$r1b = Do-Get ("/supplierInvoice?invoiceDateFrom=$yearAgo" + "&invoiceDateTo=$today" + "&from=0&count=5")

if ($r1b.Status -eq 200) {
    Write-Host "`nR1b RESULT: /supplierInvoice ACCESSIBLE — this is likely how the validator searches!"
    $parsed = $r1b.Body | ConvertFrom-Json
    Write-Host "  Existing supplier invoices count: $($parsed.values.Count)"
} elseif ($r1b.Status -eq 403) {
    Write-Host "`nR1b RESULT: /supplierInvoice BLOCKED too."
}

# R1c: Search existing vouchers to see if any created via our handler
Write-Host ""
Write-Host "========================================="
Write-Host "R1c: GET /ledger/voucher (search for existing vouchers)"
Write-Host "========================================="
$r1c = Do-Get ("/ledger/voucher?dateFrom=$yearAgo" + "&dateTo=$today" + "&from=0&count=10")
if ($r1c.Status -eq 200) {
    $parsed = $r1c.Body | ConvertFrom-Json
    Write-Host "  Existing vouchers count: $($parsed.values.Count)"
    foreach ($v in $parsed.values) {
        Write-Host "  Voucher id=$($v.id) desc='$($v.description)' type=$($v.voucherType.id) vendorInvoiceNumber='$($v.vendorInvoiceNumber)'"
    }
}

# If /supplierInvoice is accessible, let's create a test voucher and see if it appears
if ($r1b.Status -ne 200) {
    Write-Host "`nBoth /incomingInvoice and /supplierInvoice are blocked. Cannot proceed."
    $client.Dispose()
    exit 0
}

if ($r1.Status -ne 200) {
    Write-Host "`nR1 RESULT: Unexpected status $($r1.Status). Stopping."
    $client.Dispose()
    exit 0
}

Write-Host "`nR1 RESULT: ACCESSIBLE (200) — proceeding to R2"

Write-Host ""
Write-Host "========================================="
Write-Host "R2: Create test supplier + POST /incomingInvoice"
Write-Host "Purpose: Check if we can create an incoming invoice"
Write-Host "========================================="

# Step 2a: Create a test supplier
Write-Host "`n--- Step 2a: Create test supplier ---"
$supplierJson = '{"name":"TestSupplier-Verify","organizationNumber":"999888777"}'
$r2a = Do-Post "/supplier" $supplierJson
$supplierId = $null
if ($r2a.Status -eq 201 -or $r2a.Status -eq 200) {
    $parsed = $r2a.Body | ConvertFrom-Json
    $supplierId = $parsed.value.id
    Write-Host "  Supplier created: id=$supplierId"
}
else {
    Write-Host "  WARNING: Supplier creation failed. Trying to search existing..."
    $searchResult = Do-Get ("/supplier?name=TestSupplier-Verify" + "&from=0&count=1")
    if ($searchResult.Status -eq 200) {
        $parsed = $searchResult.Body | ConvertFrom-Json
        if ($parsed.values.Count -gt 0) {
            $supplierId = $parsed.values[0].id
            Write-Host "  Found existing supplier: id=$supplierId"
        }
    }
}

if (-not $supplierId) {
    Write-Host "`nR2 RESULT: Cannot create/find supplier. Stopping."
    $client.Dispose()
    exit 0
}

# Step 2b: Look up voucherType for supplier invoices
Write-Host "`n--- Step 2b: Look up voucher type ---"
$r2b = Do-Get ("/ledger/voucherType?name=Leverand%C3%B8rfaktura" + "&from=0&count=5")
$voucherTypeId = $null
if ($r2b.Status -eq 200) {
    $parsed = $r2b.Body | ConvertFrom-Json
    if ($parsed.values -and $parsed.values.Count -gt 0) {
        $voucherTypeId = $parsed.values[0].id
        Write-Host "  VoucherType found: id=$voucherTypeId"
    }
    else {
        Write-Host "  No voucherType found for 'Leverandørfaktura'"
    }
}

# Step 2c: Look up account 6500
Write-Host "`n--- Step 2c: Look up account 6500 ---"
$r2c = Do-Get ("/ledger/account?number=6500" + "&from=0&count=1")
$accountId = $null
if ($r2c.Status -eq 200) {
    $parsed = $r2c.Body | ConvertFrom-Json
    if ($parsed.values -and $parsed.values.Count -gt 0) {
        $accountId = $parsed.values[0].id
        Write-Host "  Account 6500 found: id=$accountId"
    }
}

# Step 2d: Look up VAT type (25% input)
Write-Host "`n--- Step 2d: Look up VAT type ---"
$r2d = Do-Get ("/ledger/vatType?number=1" + "&from=0&count=5")
$vatTypeId = $null
if ($r2d.Status -eq 200) {
    $parsed = $r2d.Body | ConvertFrom-Json
    if ($parsed.values -and $parsed.values.Count -gt 0) {
        $vatTypeId = $parsed.values[0].id
        Write-Host "  VatType 1 (25% input): id=$vatTypeId"
    }
}

if (-not $accountId) {
    Write-Host "  Missing account, stopping."
    $client.Dispose()
    exit 0
}

# Step 2e: POST /incomingInvoice
Write-Host "`n--- Step 2e: POST /incomingInvoice ---"
$today = (Get-Date).ToString("yyyy-MM-dd")
$due = (Get-Date).AddDays(30).ToString("yyyy-MM-dd")

$invoiceBody = @{
    invoiceHeader = @{
        vendorId      = $supplierId
        invoiceDate   = $today
        dueDate       = $due
        invoiceAmount = 10000
        description   = "Test incoming invoice for verification"
        invoiceNumber = "TEST-VERIFY-001"
    }
    orderLines    = @(
        @{
            row           = 1
            accountId     = $accountId
            amountInclVat = 10000
            description   = "Test line"
        }
    )
} | ConvertTo-Json -Depth 4

if ($vatTypeId) {
    # Re-create with vatTypeId
    $invoiceBody = @{
        invoiceHeader = @{
            vendorId      = $supplierId
            invoiceDate   = $today
            dueDate       = $due
            invoiceAmount = 10000
            description   = "Test incoming invoice for verification"
            invoiceNumber = "TEST-VERIFY-001"
        }
        orderLines    = @(
            @{
                row           = 1
                accountId     = $accountId
                amountInclVat = 10000
                vatTypeId     = $vatTypeId
                description   = "Test line"
            }
        )
    } | ConvertTo-Json -Depth 4
}

if ($voucherTypeId) {
    # Re-create with voucherTypeId
    $invoiceBody = @{
        invoiceHeader = @{
            vendorId      = $supplierId
            invoiceDate   = $today
            dueDate       = $due
            invoiceAmount = 10000
            description   = "Test incoming invoice for verification"
            invoiceNumber = "TEST-VERIFY-001"
            voucherTypeId = $voucherTypeId
        }
        orderLines    = @(
            @{
                row           = 1
                accountId     = $accountId
                amountInclVat = 10000
                vatTypeId     = $vatTypeId
                description   = "Test line"
            }
        )
    } | ConvertTo-Json -Depth 4
}

$r2e = Do-Post "/incomingInvoice" $invoiceBody

if ($r2e.Status -eq 403) {
    Write-Host "`nR2 RESULT: POST /incomingInvoice BLOCKED (403) — cannot create incoming invoices."
    Write-Host "Theory: Partially dead — endpoint is readable but not writable."
    
    # Let's also check if there's a different endpoint
    Write-Host "`n--- Bonus: Check /supplierInvoice endpoint ---"
    $bonus = Do-Get ("/supplierInvoice?from=0" + "&count=5")
    
    $client.Dispose()
    exit 0
}

if ($r2e.Status -eq 201 -or $r2e.Status -eq 200) {
    Write-Host "`nR2 RESULT: SUCCESS — incoming invoice created!"
    $parsed = $r2e.Body | ConvertFrom-Json
    $voucherId = $null
    if ($parsed.value) {
        $voucherId = $parsed.value.voucherId
        Write-Host "  VoucherId: $voucherId"
        Write-Host "  InvoiceLifeCycle: $($parsed.value.invoiceLifeCycle)"
    }
    
    Write-Host ""
    Write-Host "========================================="
    Write-Host "R3: GET /incomingInvoice/search (verify it appears)"
    Write-Host "========================================="
    
    # Search by invoice number
    $r3a = Do-Get ("/incomingInvoice/search?invoiceNumber=TEST-VERIFY-001" + "&from=0&count=5")
    
    # Also search by vendor
    $r3b = Do-Get ("/incomingInvoice/search?vendorId=$supplierId" + "&from=0&count=5")
    
    # Also try getting by voucherId
    if ($voucherId) {
        $r3c = Do-Get "/incomingInvoice/$voucherId"
    }
    
    Write-Host "`nR3 RESULT: Check output above."
}
else {
    Write-Host "`nR2 RESULT: Unexpected status $($r2e.Status)"
}

$client.Dispose()
Write-Host "`n========================================="
Write-Host "VERIFICATION COMPLETE"
Write-Host "========================================="
