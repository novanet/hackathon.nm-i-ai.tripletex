# Test-IncomingInvoice2.ps1 — Test POST /incomingInvoice for creating supplier invoices
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
$dueDate = (Get-Date).AddDays(30).ToString("yyyy-MM-dd")
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$nextMonth = (Get-Date).AddDays(60).ToString("yyyy-MM-dd")

# Setup: get account, vat, currency
$t = $client.GetAsync("$BaseUrl/ledger/account?number=6500&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$acctId = ($c.Result | ConvertFrom-Json).values[0].id

$t = $client.GetAsync("$BaseUrl/ledger/vatType?number=1&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$vatId = ($c.Result | ConvertFrom-Json).values[0].id

$t = $client.GetAsync("$BaseUrl/currency?code=NOK&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$currencyId = ($c.Result | ConvertFrom-Json).values[0].id

# Create supplier
$supBody = [System.Net.Http.StringContent]::new('{"name":"IncomingInvTest-Supplier","organizationNumber":"912399995"}', [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/supplier", $supBody); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$supplierId = ($c.Result | ConvertFrom-Json).value.id

Write-Host "Setup: acct=$acctId vat=$vatId currency=$currencyId supplier=$supplierId"

# ==== TEST 1: POST /incomingInvoice with sendTo=ledger ====
Write-Host "`n=== TEST 1: POST /incomingInvoice?sendTo=ledger ==="
$body1 = @{
    invoiceHeader = @{
        vendorId = $supplierId
        invoiceDate = $today
        dueDate = $dueDate
        currencyId = $currencyId
        invoiceAmount = 10000
        description = "Leverandorfaktura kontorrekvisita"
        invoiceNumber = "INV-12345"
    }
    orderLines = @(
        @{
            row = 1
            description = "Kontorrekvisita"
            accountId = $acctId
            amountInclVat = 10000
            vatTypeId = $vatId
        }
    )
} | ConvertTo-Json -Depth 4
Write-Host "Body: $body1"

$cnt = [System.Net.Http.StringContent]::new($body1, [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/incomingInvoice?sendTo=ledger", $cnt); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "Status: $([int]$t.Result.StatusCode)"
Write-Host "Response: $($c.Result)"
$c.Result | Out-File "$PSScriptRoot\..\src\logs\incoming_inv_ledger.json" -Encoding utf8

# Check SI
Write-Host "`n=== Check /supplierInvoice ==="
$t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$nextMonth&from=0&count=20")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$siParsed = $c.Result | ConvertFrom-Json
Write-Host "SupplierInvoices found: $($siParsed.values.Count)"
foreach ($si in $siParsed.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id)"
}

# Also check incomingInvoice/search
Write-Host "`n=== Check /incomingInvoice/search ==="
$t = $client.GetAsync("$BaseUrl/incomingInvoice/search?status=inbox,nonPosted,approval,ledger&from=0&count=20")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$iiParsed = $c.Result | ConvertFrom-Json
Write-Host "Status: $([int]$t.Result.StatusCode)"
if ([int]$t.Result.StatusCode -eq 200) {
    Write-Host "IncomingInvoices found: $($iiParsed.values.Count)"
    foreach ($ii in $iiParsed.values) {
        Write-Host "  II id=$($ii.id) invoiceNumber='$($ii.invoiceNumber)' status=$($ii.status)"
    }
} else {
    Write-Host "Response: $($c.Result.Substring(0, [Math]::Min(500, $c.Result.Length)))"
}

# ==== TEST 2: POST /incomingInvoice with sendTo=nonPosted ====
Write-Host "`n=== TEST 2: POST /incomingInvoice?sendTo=nonPosted ==="
$body2 = @{
    invoiceHeader = @{
        vendorId = $supplierId
        invoiceDate = $today
        dueDate = $dueDate
        currencyId = $currencyId
        invoiceAmount = 5000
        description = "Leverandorfaktura test 2"
        invoiceNumber = "INV-67890"
    }
    orderLines = @(
        @{
            row = 1
            description = "Diverse"
            accountId = $acctId
            amountInclVat = 5000
            vatTypeId = $vatId
        }
    )
} | ConvertTo-Json -Depth 4
$cnt2 = [System.Net.Http.StringContent]::new($body2, [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/incomingInvoice?sendTo=nonPosted", $cnt2); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
Write-Host "Status: $([int]$t.Result.StatusCode)"
Write-Host "Response: $($c.Result)"

# === Final SI check ===
Write-Host "`n=== Final /supplierInvoice check ==="
$t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$nextMonth&from=0&count=20")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$siAll = $c.Result | ConvertFrom-Json
Write-Host "SupplierInvoices total: $($siAll.values.Count)"
foreach ($si in $siAll.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id)"
}

$client.Dispose()
Write-Host "`n=== ALL DONE ==="
