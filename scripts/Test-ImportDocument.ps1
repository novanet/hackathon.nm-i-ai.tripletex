# Test-ImportDocument.ps1 — Test importDocument and properly-formatted PUT postings
$ErrorActionPreference = "Stop"

$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $SessionToken = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $SessionToken = $Matches[1].Trim() }
}
$pair = "0:$SessionToken"
$b64 = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes($pair))

$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $b64)
$client.Timeout = [TimeSpan]::FromSeconds(30)

$today = (Get-Date).ToString("yyyy-MM-dd")
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")

function Api-Get([string]$path) {
    $uri = "$BaseUrl$path"
    Write-Host "  GET $uri"
    $t = $client.GetAsync($uri); $t.Wait(); $r = $t.Result
    $c = $r.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "  => $([int]$r.StatusCode)"
    return @{ Status = [int]$r.StatusCode; Body = $c.Result }
}

function Api-Post([string]$path, [string]$json) {
    $uri = "$BaseUrl$path"
    Write-Host "  POST $uri"
    $cnt = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
    $t = $client.PostAsync($uri, $cnt); $t.Wait(); $r = $t.Result
    $c = $r.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "  => $([int]$r.StatusCode)"
    return @{ Status = [int]$r.StatusCode; Body = $c.Result }
}

function Api-Put([string]$path, [string]$json) {
    $uri = "$BaseUrl$path"
    Write-Host "  PUT $uri"
    $cnt = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $uri)
    $req.Content = $cnt
    $t = $client.SendAsync($req); $t.Wait(); $r = $t.Result
    $c = $r.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "  => $([int]$r.StatusCode)"
    return @{ Status = [int]$r.StatusCode; Body = $c.Result }
}

# ==========================================
# SETUP: Resolve IDs
# ==========================================
Write-Host "=== SETUP ==="
$r = Api-Get "/ledger/account?number=6500&count=1&fields=id,vatLocked,vatType(id)"
$acct6500Id = ($r.Body | ConvertFrom-Json).values[0].id
Write-Host "  acct6500=$acct6500Id"

$r = Api-Get "/ledger/vatType?number=1&count=1&fields=id"
$vatId = ($r.Body | ConvertFrom-Json).values[0].id
Write-Host "  vatType=$vatId"

$r = Api-Post "/supplier" '{"name":"ImportDocTest","organizationNumber":"912399991"}'
$supplierId = ($r.Body | ConvertFrom-Json).value.id
Write-Host "  supplier=$supplierId"

# ==========================================
# TEST 1: importDocument
# ==========================================
Write-Host "`n=== TEST 1: POST /ledger/voucher/importDocument ==="
$minPdf = [byte[]]@(0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x30, 0x0A, 0x31, 0x20, 0x30, 0x20, 0x6F, 0x62, 0x6A, 0x0A, 0x3C, 0x3C, 0x2F, 0x54, 0x79, 0x70, 0x65, 0x2F, 0x43, 0x61, 0x74, 0x61, 0x6C, 0x6F, 0x67, 0x2F, 0x50, 0x61, 0x67, 0x65, 0x73, 0x20, 0x32, 0x20, 0x30, 0x20, 0x52, 0x3E, 0x3E, 0x0A, 0x65, 0x6E, 0x64, 0x6F, 0x62, 0x6A, 0x0A, 0x32, 0x20, 0x30, 0x20, 0x6F, 0x62, 0x6A, 0x0A, 0x3C, 0x3C, 0x2F, 0x54, 0x79, 0x70, 0x65, 0x2F, 0x50, 0x61, 0x67, 0x65, 0x73, 0x2F, 0x4B, 0x69, 0x64, 0x73, 0x5B, 0x33, 0x20, 0x30, 0x20, 0x52, 0x5D, 0x2F, 0x43, 0x6F, 0x75, 0x6E, 0x74, 0x20, 0x31, 0x3E, 0x3E, 0x0A, 0x65, 0x6E, 0x64, 0x6F, 0x62, 0x6A, 0x0A, 0x33, 0x20, 0x30, 0x20, 0x6F, 0x62, 0x6A, 0x0A, 0x3C, 0x3C, 0x2F, 0x54, 0x79, 0x70, 0x65, 0x2F, 0x50, 0x61, 0x67, 0x65, 0x2F, 0x50, 0x61, 0x72, 0x65, 0x6E, 0x74, 0x20, 0x32, 0x20, 0x30, 0x20, 0x52, 0x2F, 0x4D, 0x65, 0x64, 0x69, 0x61, 0x42, 0x6F, 0x78, 0x5B, 0x30, 0x20, 0x30, 0x20, 0x31, 0x20, 0x31, 0x5D, 0x3E, 0x3E, 0x0A, 0x65, 0x6E, 0x64, 0x6F, 0x62, 0x6A, 0x0A, 0x78, 0x72, 0x65, 0x66, 0x0A, 0x30, 0x20, 0x34, 0x0A, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x20, 0x36, 0x35, 0x35, 0x33, 0x35, 0x20, 0x66, 0x0A, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x31, 0x30, 0x20, 0x30, 0x30, 0x30, 0x30, 0x30, 0x20, 0x6E, 0x0A, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x36, 0x34, 0x20, 0x30, 0x30, 0x30, 0x30, 0x30, 0x20, 0x6E, 0x0A, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x31, 0x32, 0x33, 0x20, 0x30, 0x30, 0x30, 0x30, 0x30, 0x20, 0x6E, 0x0A, 0x74, 0x72, 0x61, 0x69, 0x6C, 0x65, 0x72, 0x0A, 0x3C, 0x3C, 0x2F, 0x53, 0x69, 0x7A, 0x65, 0x20, 0x34, 0x2F, 0x52, 0x6F, 0x6F, 0x74, 0x20, 0x31, 0x20, 0x30, 0x20, 0x52, 0x3E, 0x3E, 0x0A, 0x73, 0x74, 0x61, 0x72, 0x74, 0x78, 0x72, 0x65, 0x66, 0x0A, 0x31, 0x39, 0x35, 0x0A, 0x25, 0x25, 0x45, 0x4F, 0x46)

$multipart = [System.Net.Http.MultipartFormDataContent]::new()
$fileContent = [System.Net.Http.ByteArrayContent]::new($minPdf)
$fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
$multipart.Add($fileContent, "file", "invoice.pdf")
$multipart.Add([System.Net.Http.StringContent]::new("Test supplier invoice import"), "description")

$uri = "$BaseUrl/ledger/voucher/importDocument"
Write-Host "  POST $uri (multipart)"
try {
    $t = $client.PostAsync($uri, $multipart); $t.Wait()
    $resp = $t.Result
    $c = $resp.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "  importDocument status: $([int]$resp.StatusCode) $($resp.StatusCode)"
    Write-Host "  importDocument body: $($c.Result.Substring(0, [Math]::Min(500, $c.Result.Length)))"
    
    if ($resp.StatusCode -eq 'Created' -or $resp.StatusCode -eq 'OK') {
        $parsed = $c.Result | ConvertFrom-Json
        $importVId = $parsed.values[0].id
        Write-Host "  ** importDocument voucherId: $importVId **"
    }
}
catch {
    Write-Host "  importDocument ERROR: $_"
}

# ==========================================
# TEST 2: Voucher + PUT postings with PROPER array body
# ==========================================
Write-Host "`n=== TEST 2: Voucher sendToLedger=false + PUT postings (array body) ==="
$r = Api-Get "/ledger/account?number=2400&count=1&fields=id"
$acct2400Id = ($r.Body | ConvertFrom-Json).values[0].id

$r = Api-Get "/ledger/voucherType?name=Leverand%C3%B8rfaktura&count=1&fields=id"
$vtId = ($r.Body | ConvertFrom-Json).values[0].id

# Create voucher WITHOUT sendToLedger (pending state)
$vBody = @{
    date                = $today
    description         = "PutTest supplier invoice"
    vendorInvoiceNumber = "PUT-TEST-001"
    voucherType         = @{ id = $vtId }
    postings            = @(
        @{ date = $today; description = "debit"; account = @{id = $acct6500Id }; amountGross = 8000; amountGrossCurrency = 8000; supplier = @{id = $supplierId }; vatType = @{id = $vatId }; row = 1 },
        @{ date = $today; description = "credit"; account = @{id = $acct2400Id }; amountGross = -8000; amountGrossCurrency = -8000; supplier = @{id = $supplierId }; row = 2 }
    )
} | ConvertTo-Json -Depth 5

$r = Api-Post "/ledger/voucher?sendToLedger=false" $vBody
$vId2 = ($r.Body | ConvertFrom-Json).value.id
Write-Host "  Voucher ID: $vId2"

# Now try PUT with EXPLICIT array wrapping (force array even with 1 element)
$putBody = '[{"posting":{"account":{"id":' + $acct6500Id + '},"amountGross":8000,"amountGrossCurrency":8000,"vatType":{"id":' + $vatId + '},"supplier":{"id":' + $supplierId + '},"description":"Debit posting via PUT"}}]'
Write-Host "  PUT body: $putBody"

$r2 = Api-Put "/supplierInvoice/voucher/$vId2/postings?sendToLedger=false" $putBody
Write-Host "  PUT result: $($r2.Body.Substring(0, [Math]::Min(500, $r2.Body.Length)))"

# Also try with sendToLedger=true
$r3 = Api-Put "/supplierInvoice/voucher/$vId2/postings?sendToLedger=true" $putBody
Write-Host "  PUT sendToLedger=true: $($r3.Body.Substring(0, [Math]::Min(500, $r3.Body.Length)))"

# ==========================================
# TEST 3: Check SupplierInvoice entities
# ==========================================
Write-Host "`n=== FINAL: Check /supplierInvoice ==="
$r = Api-Get "/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=20"
$parsed = $r.Body | ConvertFrom-Json
Write-Host "  SupplierInvoices found: $($parsed.values.Count)"
foreach ($si in $parsed.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplierId=$($si.supplier.id) voucherId=$($si.voucher.id)"
}

$client.Dispose()
Write-Host "`n=== ALL TESTS COMPLETE ==="
