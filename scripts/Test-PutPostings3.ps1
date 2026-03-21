# Test-PutPostings3.ps1 — More variations for SI creation
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

# Setup
$t = $client.GetAsync("$BaseUrl/ledger/account?number=6500&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$acctId = ($c.Result | ConvertFrom-Json).values[0].id

$t = $client.GetAsync("$BaseUrl/ledger/vatType?number=1&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$vatId = ($c.Result | ConvertFrom-Json).values[0].id

$supCnt = [System.Net.Http.StringContent]::new('{"name":"PutTest3-Sup","organizationNumber":"912399994"}', [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/supplier", $supCnt); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$supplierId = ($c.Result | ConvertFrom-Json).value.id
Write-Host "Setup: acct=$acctId vat=$vatId supplier=$supplierId"

# ==== TEST 1: POST /ledger/voucher with postings=[] (empty array) ====
Write-Host "`n=== TEST 1: POST voucher with empty postings array ==="
$body1 = '{"date":"' + $today + '","description":"Empty postings test","postings":[]}'
$cnt = [System.Net.Http.StringContent]::new($body1, [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/ledger/voucher", $cnt); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "Status: $([int]$t.Result.StatusCode)"
Write-Host "Body: $($c.Result.Substring(0, [Math]::Min(500, $c.Result.Length)))"

# ==== TEST 2: importDocument + PUT with MINIMAL posting body (no date, no desc) ====
Write-Host "`n=== TEST 2: importDocument + PUT minimal body ==="
$minPdf = [byte[]]@(0x25,0x50,0x44,0x46,0x2D,0x31,0x2E,0x30,0x0A,0x31,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x43,0x61,0x74,0x61,0x6C,0x6F,0x67,0x2F,0x50,0x61,0x67,0x65,0x73,0x20,0x32,0x20,0x30,0x20,0x52,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x32,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x50,0x61,0x67,0x65,0x73,0x2F,0x4B,0x69,0x64,0x73,0x5B,0x33,0x20,0x30,0x20,0x52,0x5D,0x2F,0x43,0x6F,0x75,0x6E,0x74,0x20,0x31,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x33,0x20,0x30,0x20,0x6F,0x62,0x6A,0x0A,0x3C,0x3C,0x2F,0x54,0x79,0x70,0x65,0x2F,0x50,0x61,0x67,0x65,0x2F,0x50,0x61,0x72,0x65,0x6E,0x74,0x20,0x32,0x20,0x30,0x20,0x52,0x2F,0x4D,0x65,0x64,0x69,0x61,0x42,0x6F,0x78,0x5B,0x30,0x20,0x30,0x20,0x31,0x20,0x31,0x5D,0x3E,0x3E,0x0A,0x65,0x6E,0x64,0x6F,0x62,0x6A,0x0A,0x78,0x72,0x65,0x66,0x0A,0x30,0x20,0x34,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x20,0x36,0x35,0x35,0x33,0x35,0x20,0x66,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x30,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x36,0x34,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x32,0x33,0x20,0x30,0x30,0x30,0x30,0x30,0x20,0x6E,0x0A,0x74,0x72,0x61,0x69,0x6C,0x65,0x72,0x0A,0x3C,0x3C,0x2F,0x53,0x69,0x7A,0x65,0x20,0x34,0x2F,0x52,0x6F,0x6F,0x74,0x20,0x31,0x20,0x30,0x20,0x52,0x3E,0x3E,0x0A,0x73,0x74,0x61,0x72,0x74,0x78,0x72,0x65,0x66,0x0A,0x31,0x39,0x35,0x0A,0x25,0x25,0x45,0x4F,0x46)

$multipart = [System.Net.Http.MultipartFormDataContent]::new()
$fileContent = [System.Net.Http.ByteArrayContent]::new($minPdf)
$fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
$multipart.Add($fileContent, "file", "invoice.pdf")
$multipart.Add([System.Net.Http.StringContent]::new("Test PUT minimal"), "description")
$t = $client.PostAsync("$BaseUrl/ledger/voucher/importDocument", $multipart); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "importDocument: $([int]$t.Result.StatusCode)"
$vid = ($c.Result | ConvertFrom-Json).values[0].id
Write-Host "VoucherId: $vid"

# PUT with minimal info (no date in posting, just account and amount)
$putBody2 = '[{"posting":{"account":{"id":' + $acctId + '},"amountGross":10000,"amountGrossCurrency":10000,"supplier":{"id":' + $supplierId + '}}}]'
Write-Host "PUT body (minimal): $putBody2"
$putUri = "$BaseUrl/supplierInvoice/voucher/$vid/postings?sendToLedger=false"
Write-Host "PUT $putUri"
$cnt2 = [System.Net.Http.StringContent]::new($putBody2, [System.Text.Encoding]::UTF8, "application/json")
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $putUri)
$req.Content = $cnt2
$t = $client.SendAsync($req); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "PUT status: $([int]$t.Result.StatusCode)"
Write-Host "PUT body: $($c.Result.Substring(0, [Math]::Min(1000, $c.Result.Length)))"

# ==== TEST 3: importDocument + PUT with amountGross only (no currency) ====
Write-Host "`n=== TEST 3: importDocument + PUT amountGross only ==="
$multipart2 = [System.Net.Http.MultipartFormDataContent]::new()
$fc2 = [System.Net.Http.ByteArrayContent]::new($minPdf)
$fc2.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
$multipart2.Add($fc2, "file", "inv3.pdf")
$multipart2.Add([System.Net.Http.StringContent]::new("Test3"), "description")
$t = $client.PostAsync("$BaseUrl/ledger/voucher/importDocument", $multipart2); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$vid3 = ($c.Result | ConvertFrom-Json).values[0].id
Write-Host "VoucherId: $vid3"

$putBody3 = '[{"posting":{"account":{"id":' + $acctId + '},"amountGross":10000}}]'
Write-Host "PUT body: $putBody3"
$putUri3 = "$BaseUrl/supplierInvoice/voucher/$vid3/postings"
$cnt3 = [System.Net.Http.StringContent]::new($putBody3, [System.Text.Encoding]::UTF8, "application/json")
$req3 = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $putUri3)
$req3.Content = $cnt3
$t = $client.SendAsync($req3); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "PUT status: $([int]$t.Result.StatusCode)"
Write-Host "PUT body: $($c.Result.Substring(0, [Math]::Min(1000, $c.Result.Length)))"

# ==== TEST 4: importDocument + PUT with only amountGross (absolute minimal) ====
Write-Host "`n=== TEST 4: importDocument + PUT absolute minimal ==="
$multipart3 = [System.Net.Http.MultipartFormDataContent]::new()
$fc3 = [System.Net.Http.ByteArrayContent]::new($minPdf)
$fc3.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
$multipart3.Add($fc3, "file", "inv4.pdf")
$multipart3.Add([System.Net.Http.StringContent]::new("Test4"), "description")
$t = $client.PostAsync("$BaseUrl/ledger/voucher/importDocument", $multipart3); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$vid4 = ($c.Result | ConvertFrom-Json).values[0].id
Write-Host "VoucherId: $vid4"

# Try just the bare minimum — only the array wrapper
$putBody4 = '[]'
$putUri4 = "$BaseUrl/supplierInvoice/voucher/$vid4/postings"
$cnt4 = [System.Net.Http.StringContent]::new($putBody4, [System.Text.Encoding]::UTF8, "application/json")
$req4 = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $putUri4)
$req4.Content = $cnt4
$t = $client.SendAsync($req4); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "PUT empty array status: $([int]$t.Result.StatusCode)"
Write-Host "PUT body: $($c.Result.Substring(0, [Math]::Min(1000, $c.Result.Length)))"

# === Final SI check ===
Write-Host "`n=== Final /supplierInvoice check ==="
$t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$tomorrow&from=0&count=20")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$siParsed = $c.Result | ConvertFrom-Json
Write-Host "SupplierInvoices: $($siParsed.values.Count)"
foreach ($si in $siParsed.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id) voucher=$($si.voucher.id)"
}

# === Also try broader search without date filter ===
Write-Host "`n=== Broader /supplierInvoice search (no date filter) ==="
$t = $client.GetAsync("$BaseUrl/supplierInvoice?from=0&count=5")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$siAll = $c.Result | ConvertFrom-Json
Write-Host "SupplierInvoices (all): $($siAll.values.Count)"
foreach ($si in $siAll.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)'"
}

$client.Dispose()
Write-Host "`n=== ALL DONE ==="
