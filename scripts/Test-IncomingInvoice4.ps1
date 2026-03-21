# Test-IncomingInvoice4.ps1 — Test all sendTo options
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

# Use existing supplier
$supplierId = 108333585  
Write-Host "Setup: acct=$acctId vat=$vatId currency=$currencyId supplier=$supplierId"

$uniqueId = [Guid]::NewGuid().ToString()
$bodyJson = '{"invoiceHeader":{"vendorId":' + $supplierId + ',"invoiceDate":"' + $today + '","dueDate":"' + $dueDate + '","currencyId":' + $currencyId + ',"invoiceAmount":10000,"description":"Leverandorfaktura test","invoiceNumber":"INV-' + $uniqueId.Substring(0,6) + '"},"orderLines":[{"externalId":"' + $uniqueId + '","row":1,"description":"Kontorrekvisita","accountId":' + $acctId + ',"amountInclVat":10000,"vatTypeId":' + $vatId + '}]}'

foreach ($sendTo in @("nonPosted", "inbox", "")) {
    $label = if ($sendTo -eq "") { "(default/no param)" } else { $sendTo }
    Write-Host "`n=== POST /incomingInvoice sendTo=$label ==="
    $url = if ($sendTo -eq "") { "$BaseUrl/incomingInvoice" } else { "$BaseUrl/incomingInvoice?sendTo=$sendTo" }
    
    # Use unique externalId for each attempt
    $uid = [Guid]::NewGuid().ToString()
    $thisBody = '{"invoiceHeader":{"vendorId":' + $supplierId + ',"invoiceDate":"' + $today + '","dueDate":"' + $dueDate + '","currencyId":' + $currencyId + ',"invoiceAmount":10000,"description":"Leverandorfaktura test ' + $label + '","invoiceNumber":"INV-' + $uid.Substring(0,6) + '"},"orderLines":[{"externalId":"' + $uid + '","row":1,"description":"Kontorrekvisita","accountId":' + $acctId + ',"amountInclVat":10000,"vatTypeId":' + $vatId + '}]}'
    
    $cnt = [System.Net.Http.StringContent]::new($thisBody, [System.Text.Encoding]::UTF8, "application/json")
    $t = $client.PostAsync($url, $cnt); $t.Wait()
    $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
    Write-Host "Status: $([int]$t.Result.StatusCode)"
    Write-Host "Response: $($c.Result.Substring(0, [Math]::Min(800, $c.Result.Length)))"
    
    if ([int]$t.Result.StatusCode -ge 200 -and [int]$t.Result.StatusCode -lt 300) {
        Write-Host "*** SUCCESS! ***"
        $c.Result | Out-File "$PSScriptRoot\..\src\logs\incoming_inv_success.json" -Encoding utf8
    }
}

# Check SI
Write-Host "`n=== /supplierInvoice check ==="
$t = $client.GetAsync("$BaseUrl/supplierInvoice?invoiceDateFrom=$yesterday&invoiceDateTo=$nextMonth&from=0&count=20")
$t.Wait(); $c = $t.Result.Content.ReadAsStringAsync(); $c.Wait()
$siParsed = $c.Result | ConvertFrom-Json
Write-Host "SupplierInvoices: $($siParsed.values.Count)"
foreach ($si in $siParsed.values) {
    Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount)"
}

$client.Dispose()
Write-Host "`n=== DONE ==="
