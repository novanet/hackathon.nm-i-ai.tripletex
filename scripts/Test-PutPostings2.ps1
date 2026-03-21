# Test-PutPostings2.ps1 — Test PUT postings with sendToLedger=false and other variations
$ErrorActionPreference = "Stop"

$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $SessionToken = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $SessionToken = $Matches[1].Trim() }
}
$b64 = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("0:$SessionToken"))
$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $b64)
$client.Timeout = [TimeSpan]::FromSeconds(30)

$today = (Get-Date).ToString("yyyy-MM-dd")
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")

# Setup refs
$t = $client.GetAsync("$BaseUrl/ledger/account?number=6500&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$acctId = ($c.Result | ConvertFrom-Json).values[0].id

$t = $client.GetAsync("$BaseUrl/ledger/vatType?number=1&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$vatId = ($c.Result | ConvertFrom-Json).values[0].id

$supBody = [System.Net.Http.StringContent]::new('{"name":"PutTest2-Supplier","organizationNumber":"912399993"}', [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/supplier", $supBody); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$supplierId = ($c.Result | ConvertFrom-Json).value.id
Write-Host "Setup: acct=$acctId vat=$vatId supplier=$supplierId"

# ======= importDocument =======
Write-Host "`n=== importDocument ==="
$minPdf = [byte[]]@(0x25,0x50,0x44,0x46,0x2D,0x31,0x2E,0x30,0x0A,0x31,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x43,0x61,0x74,0x61,0x6C,0x6F,0x67,0x2F,0x50,0x61,0x67,0x65,0x73,0x20,0x32,0x20,0x30,0x20,0x52,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x32,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x50,0x61,0x67,0x65,0x73,0x2F,0x4B,0x69,0x64,0x73,0x5B,0x33,0x20,0x30,0x20,0x52,0x5D,0x2F,0x43,0x6F,0x75,0x6E,0x74,0x20,0x31,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x33,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x50,0x61,0x67,0x65,0x2F,0x50,0x61,0x72,0x65,0x6E,0x74,0x20,0x32,0x20,0x30,0x20,0x52,0x2F,0x4D,0x65,0x64,0x69,0x61,0x42,0x6F,0x78,0x5B,0x30,0x20,0x30,0x20,0x31,0x20,0x31,0x5D,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x78,0x72,0x65,0x66,0x0A,0x30,0x20,0x34,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x20,0x36,0x35,0x35,0x33,0x35,0x20,0x66,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x30,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x36,0x34,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x32,0x33,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x74,0x72,0x61,0x69,0x6C,0x65,0x72,0x0A,0x3C,0x3C,0x2F,0x53,0x69,0x7A,0x65,0x20,0x34,0x2F,0x52,0x6F,0x6F,0x74,0x20,0x31,0x20,0x30,0x20,0x52,0x3E,0x3E,0x0A,0x73,0x74,0x61,0x72,0x74,0x78,0x72,0x65,0x66,0x0A,0x31,0x39,0x35,0x0A,0x25,0x25,0x45,0x4F,0x46)

$multipart = [System.Net.Http.MultipartFormDataContent]::new()
$fileContent = [System.Net.Http.ByteArrayContent]::new($minPdf)
$fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
$multipart.Add($fileContent, "file", "invoice.pdf")
$multipart.Add([System.Net.Http.StringContent]::new("Leverandorfaktura PutTest2"), "description")

$t = $client.PostAsync("$BaseUrl/ledger/voucher/importDocument", $multipart); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "importDocument: $([int]$t.Result.StatusCode)"
$emptyVoucherId = ($c.Result | ConvertFrom-Json).values[0].id
Write-Host "Empty voucher: $emptyVoucherId"

# ======= TEST A: PUT with sendToLedger=false =======
Write-Host "`n=== TEST A: PUT postings sendToLedger=FALSE ==="
$putBody = '[{"posting":{"account":{"id":' + $acctId + '},"amountGross":10000,"amountGrossCurrency":10000,"vatType":{"id":' + $vatId + '},"supplier":{"id":' + $supplierId + '},"description":"Kontorrekvisita","date":"' + $today + '"}}]'
Write-Host "Body: $putBody"

$putUri = "$BaseUrl/supplierInvoice/voucher/$emptyVoucherId/postings?sendToLedger=false&voucherDate=$today"
Write-Host "PUT $putUri"
$cnt = [System.Net.Http.StringContent]::new($putBody, [System.Text.Encoding]::UTF8, "application/json")
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $putUri)
$req.Content = $cnt
$t = $client.SendAsync($req); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "PUT status: $([int]$t.Result.StatusCode)"
$putResult = $c.Result
if ($putResult.Length -gt 1500) { Write-Host "PUT body (truncated): $($putResult.Substring(0,1500))..." } else { Write-Host "PUT body: $putResult" }
$putResult | Out-File "$PSScriptRoot\..\src\logs\put_postings_false.json" -Encoding utf8

# ======= Check SI =======
Write-Host "`n=== Check /supplierInvoice ==="
$t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=20")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$siParsed = $c.Result | ConvertFrom-Json
Write-Host "SupplierInvoices found: $($siParsed.values.Count)"
foreach ($si in $siParsed.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
}
$c.Result | Out-File "$PSScriptRoot\..\src\logs\si_after_false.json" -Encoding utf8

# ======= If PUT succeeded but no SI, try without voucherDate =======
if ($siParsed.values.Count -eq 0 -and [int]$t.Result.StatusCode -lt 300) {
    Write-Host "`n=== TEST B: new importDocument + PUT without voucherDate ==="
    $multipart2 = [System.Net.Http.MultipartFormDataContent]::new()
    $fc2 = [System.Net.Http.ByteArrayContent]::new($minPdf)
    $fc2.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
    $multipart2.Add($fc2, "file", "invoice2.pdf")
    $multipart2.Add([System.Net.Http.StringContent]::new("Leverandorfaktura PutTest2b"), "description")
    $t = $client.PostAsync("$BaseUrl/ledger/voucher/importDocument", $multipart2); $t.Wait()
    $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    $vid2 = ($c.Result | ConvertFrom-Json).values[0].id
    Write-Host "Empty voucher B: $vid2"
    
    $putUri2 = "$BaseUrl/supplierInvoice/voucher/$vid2/postings?sendToLedger=false"
    Write-Host "PUT $putUri2"
    $cnt2 = [System.Net.Http.StringContent]::new($putBody, [System.Text.Encoding]::UTF8, "application/json")
    $req2 = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $putUri2)
    $req2.Content = $cnt2
    $t = $client.SendAsync($req2); $t.Wait()
    $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "PUT B status: $([int]$t.Result.StatusCode)"
    if ($c.Result.Length -gt 1500) { Write-Host "PUT B body (truncated): $($c.Result.Substring(0,1500))..." } else { Write-Host "PUT B body: $($c.Result)" }
    $c.Result | Out-File "$PSScriptRoot\..\src\logs\put_postings_nodate.json" -Encoding utf8
    
    # Check SI again
    $t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=20")
    $t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    $siParsed2 = $c.Result | ConvertFrom-Json
    Write-Host "SupplierInvoices found after B: $($siParsed2.values.Count)"
    foreach ($si in $siParsed2.values) {
        Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
    }
}

# ======= Also try: POST voucher WITHOUT postings, then PUT =======
Write-Host "`n=== TEST C: POST voucher WITHOUT postings (no sendToLedger), then PUT ==="
$emptyVoucherBody = '{"date":"' + $today + '","description":"Empty voucher for SI","vendorInvoiceNumber":"EMPTY-001"}'
Write-Host "POST $BaseUrl/ledger/voucher"
$cnt3 = [System.Net.Http.StringContent]::new($emptyVoucherBody, [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/ledger/voucher", $cnt3); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "POST empty voucher: $([int]$t.Result.StatusCode)"
Write-Host "Body: $($c.Result.Substring(0, [Math]::Min(500, $c.Result.Length)))"
if ([int]$t.Result.StatusCode -lt 300) {
    $emptyVId = ($c.Result | ConvertFrom-Json).value.id
    Write-Host "Empty voucher C: $emptyVId"
    
    $putUri3 = "$BaseUrl/supplierInvoice/voucher/$emptyVId/postings?sendToLedger=false"
    Write-Host "PUT $putUri3"
    $cnt4 = [System.Net.Http.StringContent]::new($putBody, [System.Text.Encoding]::UTF8, "application/json")
    $req3 = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $putUri3)
    $req3.Content = $cnt4
    $t = $client.SendAsync($req3); $t.Wait()
    $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "PUT C status: $([int]$t.Result.StatusCode)"
    if ($c.Result.Length -gt 1500) { Write-Host "PUT C body (truncated): $($c.Result.Substring(0,1500))..." } else { Write-Host "PUT C body: $($c.Result)" }
    $c.Result | Out-File "$PSScriptRoot\..\src\logs\put_postings_empty.json" -Encoding utf8
    
    # Final check
    $t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=20")
    $t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    $siParsed3 = $c.Result | ConvertFrom-Json
    Write-Host "SupplierInvoices found after C: $($siParsed3.values.Count)"
    foreach ($si in $siParsed3.values) {
        Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
    }
}

$client.Dispose()
Write-Host "`n=== ALL TESTS DONE ==="
