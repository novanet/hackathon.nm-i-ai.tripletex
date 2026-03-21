# Test-SupplierInvoiceVoucherPostings.ps1
# Tests the hypothesis: POST /ledger/voucher WITHOUT sendToLedger creates a pending SupplierInvoice
# Then PUT /supplierInvoice/voucher/{id}/postings books it and returns a SupplierInvoice
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
    if ($body.Length -gt 1200) { Write-Host "  Body: $($body.Substring(0, 1200))..." } else { Write-Host "  Body: $body" }
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
    if ($body.Length -gt 1200) { Write-Host "  Body: $($body.Substring(0, 1200))..." } else { Write-Host "  Body: $body" }
    return @{ Status = [int]$resp.StatusCode; Body = $body }
}

function Do-Put([string]$path, [string]$jsonBody) {
    $uri = "$BaseUrl$path"
    Write-Host "  PUT $uri"
    Write-Host "  Payload: $jsonBody"
    $content = [System.Net.Http.StringContent]::new($jsonBody, [System.Text.Encoding]::UTF8, "application/json")
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $uri)
    $req.Content = $content
    $task = $client.SendAsync($req)
    $task.Wait()
    $resp = $task.Result
    $ct = $resp.Content.ReadAsStringAsync()
    $ct.Wait()
    Write-Host "  Status: $([int]$resp.StatusCode) $($resp.StatusCode)"
    $body = $ct.Result
    if ($body.Length -gt 1200) { Write-Host "  Body: $($body.Substring(0, 1200))..." } else { Write-Host "  Body: $body" }
    return @{ Status = [int]$resp.StatusCode; Body = $body }
}

$today = (Get-Date).ToString("yyyy-MM-dd")
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")

# Setup: resolve IDs
Write-Host "=== SETUP: Resolve account IDs ==="
$r_acct6500 = Do-Get "/ledger/account?number=6500&count=1&fields=id,number,vatType(id),vatLocked"
$acct6500Id = $null; $vatId = $null
if ($r_acct6500.Status -eq 200) {
    $p = $r_acct6500.Body | ConvertFrom-Json
    if ($p.values.Count -gt 0) {
        $acct6500Id = $p.values[0].id
        $vatLocked = $p.values[0].vatLocked
        if (-not $vatLocked -and $p.values[0].vatType.id) { $vatId = $p.values[0].vatType.id }
        Write-Host "  Account 6500 id=$acct6500Id  vatLocked=$vatLocked"
    }
}

$r_acct2400 = Do-Get "/ledger/account?number=2400&count=1&fields=id"
$acct2400Id = $null
if ($r_acct2400.Status -eq 200) {
    $p = $r_acct2400.Body | ConvertFrom-Json
    if ($p.values.Count -gt 0) { $acct2400Id = $p.values[0].id; Write-Host "  Account 2400 id=$acct2400Id" }
}

# Get VAT type (id=1 = standard 25% input)
Write-Host "`n=== Resolve VAT type ==="
$r_vat = Do-Get "/ledger/vatType?number=1&count=1&fields=id,number,name"
if ($r_vat.Status -eq 200) {
    $p = $r_vat.Body | ConvertFrom-Json
    if ($p.values.Count -gt 0) { $vatId = $p.values[0].id; Write-Host "  VAT type id=$vatId" }
}

# Get voucher type
Write-Host "`n=== Resolve voucher type ==="
$r_vt = Do-Get "/ledger/voucherType?name=Leverand%C3%B8rfaktura&count=5&fields=id,name"
$voucherTypeId = $null
if ($r_vt.Status -eq 200) {
    $p = $r_vt.Body | ConvertFrom-Json
    if ($p.values.Count -gt 0) { $voucherTypeId = $p.values[0].id; Write-Host "  VoucherType id=$voucherTypeId name=$($p.values[0].name)" }
}

# Create supplier
Write-Host "`n=== Create supplier ==="
$r_sup = Do-Post "/supplier" '{"name":"PostingsTest-Supplier","organizationNumber":"912345670"}'
$supplierId = $null
if ($r_sup.Status -eq 201) {
    $p = $r_sup.Body | ConvertFrom-Json
    $supplierId = $p.value.id
    Write-Host "  Supplier ID: $supplierId"
}
if (-not $supplierId) { Write-Host "ERROR: Supplier creation failed"; $client.Dispose(); exit 1 }

# =====================================================================
# APPROACH 1: POST /ledger/voucher WITHOUT sendToLedger=true
# If ELECTRONIC_VOUCHERS creates a pending SupplierInvoice, it should appear
# =====================================================================

Write-Host "`n========= APPROACH 1: Create voucher WITHOUT sendToLedger ========="
$vBody1 = @{
    date                = $today
    description         = "PostingsTest - leverandørfaktura"
    vendorInvoiceNumber = "PT-INV-001"
    postings            = @(
        @{
            date                = $today
            description         = "PostingsTest debit"
            account             = @{ id = $acct6500Id }
            amountGross         = 12500
            amountGrossCurrency = 12500
            supplier            = @{ id = $supplierId }
            vatType             = @{ id = $vatId }
            row                 = 1
        }
        @{
            date                = $today
            description         = "PostingsTest credit"
            account             = @{ id = $acct2400Id }
            amountGross         = -12500
            amountGrossCurrency = -12500
            supplier            = @{ id = $supplierId }
            row                 = 2
        }
    )
}
if ($voucherTypeId) { $vBody1["voucherType"] = @{ id = $voucherTypeId } }

$r1 = Do-Post "/ledger/voucher?sendToLedger=false" ($vBody1 | ConvertTo-Json -Depth 5)
$voucherId1 = $null
if ($r1.Status -eq 201 -or $r1.Status -eq 200) {
    $p = $r1.Body | ConvertFrom-Json
    $voucherId1 = $p.value.id
    Write-Host "  ** Voucher ID (NO sendToLedger): $voucherId1 **"
}
else {
    Write-Host "  ERROR: Voucher creation failed"
    $client.Dispose(); exit 1
}

Write-Host "`n--- Check /supplierInvoice after voucher WITHOUT sendToLedger ---"
$r1_si = Do-Get "/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=5"
if ($r1_si.Status -eq 200) {
    $p = $r1_si.Body | ConvertFrom-Json
    Write-Host "  ** SupplierInvoices found (all): $($p.values.Count) **"
    foreach ($si in $p.values) {
        Write-Host "    SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
    }
}
$r1_siv = Do-Get "/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&voucherId=$voucherId1&from=0&count=5"
if ($r1_siv.Status -eq 200) {
    $p = $r1_siv.Body | ConvertFrom-Json
    Write-Host "  ** SupplierInvoices by voucherId: $($p.values.Count) **"
}

Write-Host "`n--- Check /supplierInvoice/forApproval after voucher WITHOUT sendToLedger ---"
$r1_fa = Do-Get "/supplierInvoice/forApproval?from=0&count=5"
if ($r1_fa.Status -eq 200) {
    $p = $r1_fa.Body | ConvertFrom-Json
    Write-Host "  ** SupplierInvoices forApproval: $($p.values.Count) **"
    foreach ($si in $p.values) {
        Write-Host "    SI id=$($si.id) voucher=$($si.voucher.id)"
    }
}

# =====================================================================
# APPROACH 2: Create a new supplier + voucher WITH sendToLedger=true,
# then call PUT /supplierInvoice/voucher/{id}/postings?sendToLedger=false
# to see if this transforms it into a SupplierInvoice
# =====================================================================

Write-Host "`n========= APPROACH 2: Voucher WITH sendToLedger=true, THEN PUT /supplierInvoice/voucher/{id}/postings ========="
$r_sup2 = Do-Post "/supplier" '{"name":"PostingsTest-Supplier2","organizationNumber":"912345688"}'
$supplierId2 = $null
if ($r_sup2.Status -eq 201) { $p = $r_sup2.Body | ConvertFrom-Json; $supplierId2 = $p.value.id; Write-Host "  Supplier2 ID: $supplierId2" }

$vBody2 = @{
    date                = $today
    description         = "PostingsTest2 - leverandørfaktura"
    vendorInvoiceNumber = "PT-INV-002"
    postings            = @(
        @{
            date                = $today
            description         = "PostingsTest2 debit"
            account             = @{ id = $acct6500Id }
            amountGross         = 6250
            amountGrossCurrency = 6250
            supplier            = @{ id = $supplierId2 }
            vatType             = @{ id = $vatId }
            row                 = 1
        }
        @{
            date                = $today
            description         = "PostingsTest2 credit"
            account             = @{ id = $acct2400Id }
            amountGross         = -6250
            amountGrossCurrency = -6250
            supplier            = @{ id = $supplierId2 }
            row                 = 2
        }
    )
}
if ($voucherTypeId) { $vBody2["voucherType"] = @{ id = $voucherTypeId } }

$r2_v = Do-Post "/ledger/voucher?sendToLedger=true" ($vBody2 | ConvertTo-Json -Depth 5)
$voucherId2 = $null
if ($r2_v.Status -eq 201) {
    $p = $r2_v.Body | ConvertFrom-Json
    $voucherId2 = $p.value.id
    Write-Host "  ** Voucher ID (WITH sendToLedger): $voucherId2 **"
}

Write-Host "`n--- Try PUT /supplierInvoice/voucher/{id}/postings?sendToLedger=false ---"
# body: array of OrderLinePosting with posting.account and posting.amountGross
$putBody = @(
    @{
        posting = @{
            account             = @{ id = $acct6500Id }
            amountGross         = 6250
            amountGrossCurrency = 6250
            vatType             = @{ id = $vatId }
            description         = "Debit posting via PUT"
        }
    }
) | ConvertTo-Json -Depth 5
$r2_put = Do-Put "/supplierInvoice/voucher/$voucherId2/postings?sendToLedger=false" $putBody
Write-Host "  PUT result:"

# Also try with sendToLedger=true
Write-Host "`n--- Try PUT /supplierInvoice/voucher/{id}/postings?sendToLedger=true ---"
if ($voucherId1) {
    $putBody2 = @(
        @{
            posting = @{
                account             = @{ id = $acct6500Id }
                amountGross         = 12500
                amountGrossCurrency = 12500
                vatType             = @{ id = $vatId }
                description         = "Debit posting via PUT sendToLedger=true"
            }
        }
    ) | ConvertTo-Json -Depth 5
    $r_put2 = Do-Put "/supplierInvoice/voucher/$voucherId1/postings?sendToLedger=true" $putBody2
    Write-Host "  PUT sendToLedger=true result:"
}

Write-Host "`n--- Final check: GET /supplierInvoice after all attempts ---"
$r_final = Do-Get "/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=10"
if ($r_final.Status -eq 200) {
    $p = $r_final.Body | ConvertFrom-Json
    Write-Host "  ** TOTAL SupplierInvoices found at end: $($p.values.Count) **"
    foreach ($si in $p.values) {
        Write-Host "    SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
    }
}

$client.Dispose()
Write-Host "`n========== TEST COMPLETE =========="
