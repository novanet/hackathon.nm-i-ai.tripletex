# Test-IncomingInvoice3.ps1 — Fixed: add externalId to orderLines
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

# Setup
$t = $client.GetAsync("$BaseUrl/ledger/account?number=6500&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$acctId = ($c.Result | ConvertFrom-Json).values[0].id

$t = $client.GetAsync("$BaseUrl/ledger/vatType?number=1&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$vatId = ($c.Result | ConvertFrom-Json).values[0].id

$t = $client.GetAsync("$BaseUrl/currency?code=NOK&count=1&fields=id"); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$currencyId = ($c.Result | ConvertFrom-Json).values[0].id

$supBody = [System.Net.Http.StringContent]::new('{"name":"IncomingInv3-Supplier","organizationNumber":"912399996"}', [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/supplier", $supBody); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$supplierId = ($c.Result | ConvertFrom-Json).value.id
Write-Host "Setup: acct=$acctId vat=$vatId currency=$currencyId supplier=$supplierId"

# ==== POST /incomingInvoice?sendTo=ledger (with externalId) ====
Write-Host "`n=== POST /incomingInvoice?sendTo=ledger ==="
$uniqueId = [Guid]::NewGuid().ToString()
$body = @{
    invoiceHeader = @{
        vendorId = $supplierId
        invoiceDate = $today
        dueDate = $dueDate
        currencyId = $currencyId
        invoiceAmount = 10000
        description = "Leverandorfaktura kontorrekvisita"
        invoiceNumber = "INV-TEST-001"
    }
    orderLines = @(
        @{
            externalId = $uniqueId
            row = 1
            description = "Kontorrekvisita"
            accountId = $acctId
            amountInclVat = 10000
            vatTypeId = $vatId
        }
    )
} | ConvertTo-Json -Depth 4
Write-Host "Body: $body"

$cnt = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
$t = $client.PostAsync("$BaseUrl/incomingInvoice?sendTo=ledger", $cnt); $t.Wait()
$c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$statusCode = [int]$t.Result.StatusCode
Write-Host "Status: $statusCode"
$response = $c.Result
Write-Host "Response: $response"
$response | Out-File "$PSScriptRoot\..\src\logs\incoming_inv3.json" -Encoding utf8

if ($statusCode -ge 200 -and $statusCode -lt 300) {
    Write-Host "SUCCESS! Extracting voucherId..."
    $parsed = $response | ConvertFrom-Json
    Write-Host "Parsed response value: $($parsed | ConvertTo-Json -Depth 5)"
}

# Check SI
Write-Host "`n=== Check /supplierInvoice ==="
$t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$nextMonth&from=0&count=20")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$siParsed = $c.Result | ConvertFrom-Json
Write-Host "SupplierInvoices found: $($siParsed.values.Count)"
foreach ($si in $siParsed.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) supplier=$($si.supplier.id)"
}
$c.Result | Out-File "$PSScriptRoot\..\src\logs\si_after_incoming.json" -Encoding utf8

# If we got a 422, try again with sendTo=nonPosted (may have more relaxed validation)
if ($statusCode -eq 422) {
    Write-Host "`n=== RETRY: POST /incomingInvoice?sendTo=nonPosted ==="
    $cnt2 = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
    $t = $client.PostAsync("$BaseUrl/incomingInvoice?sendTo=nonPosted", $cnt2); $t.Wait()
    $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "Status: $([int]$t.Result.StatusCode)"
    Write-Host "Response: $($c.Result)"
    
    if ([int]$t.Result.StatusCode -ge 200 -and [int]$t.Result.StatusCode -lt 300) {
        Write-Host "SUCCESS with nonPosted!"
    }
    
    # Also try sendTo=inbox
    Write-Host "`n=== RETRY: POST /incomingInvoice?sendTo=inbox ==="
    $cnt3 = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
    $t = $client.PostAsync("$BaseUrl/incomingInvoice?sendTo=inbox", $cnt3); $t.Wait()
    $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "Status: $([int]$t.Result.StatusCode)"
    Write-Host "Response: $($c.Result)"
    
    if ([int]$t.Result.StatusCode -ge 200 -and [int]$t.Result.StatusCode -lt 300) {
        Write-Host "SUCCESS with inbox!"
    }
    
    # Also try without sendTo (default=inbox)
    Write-Host "`n=== RETRY: POST /incomingInvoice (no sendTo) ==="
    $cnt4 = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
    $t = $client.PostAsync("$BaseUrl/incomingInvoice", $cnt4); $t.Wait()
    $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "Status: $([int]$t.Result.StatusCode)"
    Write-Host "Response: $($c.Result)"
    
    if ([int]$t.Result.StatusCode -ge 200 -and [int]$t.Result.StatusCode -lt 300) {
        Write-Host "SUCCESS with default!"
        $parsed = $c.Result | ConvertFrom-Json
        Write-Host "Response value: $($parsed | ConvertTo-Json -Depth 5)"
        
        # Final SI check
        $t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$nextMonth&from=0&count=20")
        $t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
        $siAll = $c.Result | ConvertFrom-Json
        Write-Host "SupplierInvoices: $($siAll.values.Count)"
    }
}

$client.Dispose()
Write-Host "`n=== DONE ==="
