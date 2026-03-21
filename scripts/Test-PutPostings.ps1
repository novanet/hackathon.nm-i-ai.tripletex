# Test-PutPostings.ps1 — PUT postings on the importDocument-created empty voucher
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

# Step 1: Create fresh importDocument voucher
Write-Host "=== Step 1: importDocument ==="
$minPdf = [byte[]]@(0x25,0x50,0x44,0x46,0x2D,0x31,0x2E,0x30,0x0A,0x31,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x43,0x61,0x74,0x61,0x6C,0x6F,0x67,0x2F,0x50,0x61,0x67,0x65,0x73,0x20,0x32,0x20,0x30,0x20,0x52,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x32,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x50,0x61,0x67,0x65,0x73,0x2F,0x4B,0x69,0x64,0x73,0x5B,0x33,0x20,0x30,0x20,0x52,0x5D,0x2F,0x43,0x6F,0x75,0x6E,0x74,0x20,0x31,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x33,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x50,0x61,0x67,0x65,0x2F,0x50,0x61,0x72,0x65,0x6E,0x74,0x20,0x32,0x20,0x30,0x20,0x52,0x2F,0x4D,0x65,0x64,0x69,0x61,0x42,0x6F,0x78,0x5B,0x30,0x20,0x30,0x20,0x31,0x20,0x31,0x5D,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x78,0x72,0x65,0x66,0x0A,0x30,0x20,0x34,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x20,0x36,0x35,0x35,0x33,0x35,0x20,0x66,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x30,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x36,0x34,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x32,0x33,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x74,0x72,0x61,0x69,0x6C,0x65,0x72,0x0A,0x3C,0x3C,0x2F,0x53,0x69,0x7A,0x65,0x20,0x34,0x2F,0x52,0x6F,0x6F,0x74,0x20,0x31,0x20,0x30,0x20,0x52,0x3E,0x3E,0x0A,0x73,0x74,0x61,0x72,0x74,0x78,0x72,0x65,0x66,0x0A,0x31,0x39,0x35,0x0A,0x25,0x25,0x45,0x4F,0x46)

$multipart = [System.Net.Http.MultipartFormDataContent]::new()
$fileContent = [System.Net.Http.ByteArrayContent]::new($minPdf)
$fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
$multipart.Add($fileContent, "file", "invoice.pdf")
$multipart.Add([System.Net.Http.StringContent]::new("Leverandorfaktura test"), "description")

$uri = "$BaseUrl/ledger/voucher/importDocument"
$t = $client.PostAsync($uri, $multipart); $t.Wait()
$r = $t.Result; $c = $r.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "importDocument: $([int]$r.StatusCode)"

$parsed = $c.Result | ConvertFrom-Json
$emptyVoucherId = $parsed.values[0].id
Write-Host "Empty voucher ID: $emptyVoucherId"

# Step 2: Create a supplier
Write-Host "`n=== Step 2: Create supplier ==="
$uri2 = "$BaseUrl/supplier"
$supBody = [System.Net.Http.StringContent]::new('{"name":"PutTest-Supplier","organizationNumber":"912399992"}', [System.Text.Encoding]::UTF8, "application/json")
$t2 = $client.PostAsync($uri2, $supBody); $t2.Wait()
$r2 = $t2.Result; $c2 = $r2.Content.ReadAsStringAsync(); $c2.Wait()
Write-Host "Supplier: $([int]$r2.StatusCode)"
$supplierId = ($c2.Result | ConvertFrom-Json).value.id
Write-Host "Supplier ID: $supplierId"

# Step 3: Resolve account + VAT
Write-Host "`n=== Step 3: Resolve refs ==="
$t3 = $client.GetAsync("$BaseUrl/ledger/account?number=6500&count=1&fields=id"); $t3.Wait()
$c3 = $t3.Result.Content.ReadAsStringAsync(); $c3.Wait()
$acctId = ($c3.Result | ConvertFrom-Json).values[0].id
Write-Host "Account 6500: $acctId"

$t4 = $client.GetAsync("$BaseUrl/ledger/vatType?number=1&count=1&fields=id"); $t4.Wait()
$c4 = $t4.Result.Content.ReadAsStringAsync(); $c4.Wait()
$vatId = ($c4.Result | ConvertFrom-Json).values[0].id
Write-Host "VAT type: $vatId"

# Step 4: PUT postings on empty voucher
Write-Host "`n=== Step 4: PUT postings on importDocument voucher ==="
$putBody = '[{"posting":{"account":{"id":' + $acctId + '},"amountGross":10000,"amountGrossCurrency":10000,"vatType":{"id":' + $vatId + '},"supplier":{"id":' + $supplierId + '},"description":"Kontorrekvisita","date":"' + $today + '"}}]'
Write-Host "Body: $putBody"

$putUri = "$BaseUrl/supplierInvoice/voucher/$emptyVoucherId/postings?sendToLedger=true&voucherDate=$today"
Write-Host "PUT $putUri"
$cnt = [System.Net.Http.StringContent]::new($putBody, [System.Text.Encoding]::UTF8, "application/json")
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $putUri)
$req.Content = $cnt
$t5 = $client.SendAsync($req); $t5.Wait()
$r5 = $t5.Result; $c5 = $r5.Content.ReadAsStringAsync(); $c5.Wait()
Write-Host "PUT status: $([int]$r5.StatusCode) $($r5.StatusCode)"
$putResult = $c5.Result
Write-Host "PUT body: $putResult"

# Save full PUT response to file for inspection
$putResult | Out-File "$PSScriptRoot\..\src\logs\put_postings_result.json" -Encoding utf8

# Step 5: Check if SupplierInvoice was created
Write-Host "`n=== Step 5: Check /supplierInvoice ==="
$t6 = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=20")
$t6.Wait(); $r6 = $t6.Result; $c6 = $r6.Content.ReadAsStringAsync(); $c6.Wait()
$siParsed = $c6.Result | ConvertFrom-Json
Write-Host "SupplierInvoices found: $($siParsed.values.Count)"
foreach ($si in $siParsed.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
}
$c6.Result | Out-File "$PSScriptRoot\..\src\logs\si_after_put.json" -Encoding utf8

$client.Dispose()
Write-Host "`n=== DONE ==="
