# Test-IncomingInvoiceFixed.ps1 — Test POST /incomingInvoice with externalId field
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

$today = (Get-Date).ToString("yyyy-MM-dd")
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")
$dueDate = (Get-Date).AddDays(30).ToString("yyyy-MM-dd")

# Use the supplier we already created
$supplierId = 108303434  # VoucherTest-Supplier from previous test
$acctId = 429395431       # Account 6500
$vatId = 1                # VAT type 25% input

Write-Host "=== POST /incomingInvoice WITH externalId ==="
$iiBody = @{
    invoiceHeader = @{
        vendorId      = $supplierId
        invoiceDate   = $today
        dueDate       = $dueDate
        invoiceAmount = 5000
        description   = "Test incoming invoice with externalId"
        invoiceNumber = "TEST-VERIFY-004"
    }
    orderLines    = @(
        @{
            row           = 1
            externalId    = "line-1"
            accountId     = $acctId
            amountInclVat = 5000
            vatTypeId     = $vatId
            description   = "Test line"
        }
    )
} | ConvertTo-Json -Depth 4

$r1 = Do-Post "/incomingInvoice" $iiBody

if ($r1.Status -eq 201 -or $r1.Status -eq 200) {
    $parsed = $r1.Body | ConvertFrom-Json
    $voucherId = $parsed.value.voucherId
    $lifecycle = $parsed.value.invoiceLifeCycle
    Write-Host "`n  *** SUCCESS! VoucherId=$voucherId LifeCycle=$lifecycle ***"

    # Now check if it appears in /supplierInvoice
    Write-Host "`n=== Check /supplierInvoice by date range ==="
    $r2 = Do-Get ("/supplierInvoice?invoiceDateFrom=$yesterday" + "&invoiceDateTo=$tomorrow" + "&from=0&count=10")
    if ($r2.Status -eq 200) {
        $parsed2 = $r2.Body | ConvertFrom-Json
        Write-Host "`n  *** SUPPLIER INVOICES FOUND: $($parsed2.values.Count) ***"
        foreach ($si in $parsed2.values) {
            Write-Host "  SI id=$($si.id) invoiceNumber='$($si.invoiceNumber)' amount=$($si.amount) voucher.id=$($si.voucher.id)"
        }
    }

    # Search by voucherId
    Write-Host "`n=== Check /supplierInvoice by voucherId ==="
    $r3 = Do-Get ("/supplierInvoice?invoiceDateFrom=$yesterday" + "&invoiceDateTo=$tomorrow" + "&voucherId=$voucherId" + "&from=0&count=5")
    if ($r3.Status -eq 200) {
        $parsed3 = $r3.Body | ConvertFrom-Json
        Write-Host "`n  *** SUPPLIER INVOICES MATCHING VOUCHER: $($parsed3.values.Count) ***"
    }

    # Direct GET
    Write-Host "`n=== GET /supplierInvoice/{voucherId} directly ==="
    $r4 = Do-Get "/supplierInvoice/$voucherId"
}
else {
    Write-Host "`n  ERROR: POST /incomingInvoice failed with $($r1.Status)"
    Write-Host "  Need to check validation errors and fix."
}

$client.Dispose()
Write-Host "`n========================================="
Write-Host "VERIFICATION COMPLETE"
Write-Host "========================================="
