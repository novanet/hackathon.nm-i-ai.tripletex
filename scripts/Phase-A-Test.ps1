<#
.SYNOPSIS
    Phase A: Test whether importDocument creates SupplierInvoice entities
    and verify multiple lookup paths (voucherId, /forApproval, /incomingInvoice).
    Uses HttpClient to match the exact multipart construction used by TripletexApiClient.cs
#>
param(
    [string]$BaseUrl,
    [string]$SessionToken
)

# Load from user-secrets if not supplied
if (-not $BaseUrl -or -not $SessionToken) {
    $secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
    foreach ($line in $secretsJson) {
        if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$' -and -not $BaseUrl) { $BaseUrl = $Matches[1].Trim() }
        if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$' -and -not $SessionToken) { $SessionToken = $Matches[1].Trim() }
    }
}

$b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$SessionToken"))
$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $b64)
$client.Timeout = [TimeSpan]::FromSeconds(30)

function ApiGet($path) {
    $resp = $client.GetAsync("$BaseUrl$path").GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ S = [int]$resp.StatusCode; B = $body }
}

function ApiPostJson($path, $json) {
    $content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, "application/json")
    $resp = $client.PostAsync("$BaseUrl$path", $content).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ S = [int]$resp.StatusCode; B = $body }
}

function ApiPutJson($path, $json) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, "$BaseUrl$path")
    $req.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, "application/json")
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ S = [int]$resp.StatusCode; B = $body }
}

# Exact same minimal PDF as VoucherHandler.cs MinimalPdf field
$minimalPdf = [Text.Encoding]::ASCII.GetBytes(
    "%PDF-1.4`n1 0 obj`n<< /Type /Catalog /Pages 2 0 R >>`nendobj`n" +
    "2 0 obj`n<< /Type /Pages /Kids [3 0 R] /Count 1 >>`nendobj`n" +
    "3 0 obj`n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>`nendobj`n" +
    "xref`n0 4`n0000000000 65535 f `n0000000009 00000 n `n0000000058 00000 n `n0000000115 00000 n `n" +
    "trailer`n<< /Size 4 /Root 1 0 R >>`nstartxref`n190`n%%EOF`n")

function ApiPostMultipart($path) {
    $multipart = [System.Net.Http.MultipartFormDataContent]::new([Guid]::NewGuid().ToString("N"))
    $fileContent = [System.Net.Http.ByteArrayContent]::new($minimalPdf)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
    $multipart.Add($fileContent, "file", "invoice.pdf")
    $resp = $client.PostAsync("$BaseUrl$path", $multipart).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ S = [int]$resp.StatusCode; B = $body }
}

$today = (Get-Date).ToString("yyyy-MM-dd")
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")

Write-Host "=================================================="
Write-Host "  PHASE A: importDocument -> SupplierInvoice paths"
Write-Host "  BaseUrl: $BaseUrl"
Write-Host "=================================================="

# BASELINE
Write-Host ""
Write-Host "[BASELINE] GET /supplierInvoice (date range required)"
$rb = ApiGet "/supplierInvoice?invoiceDateFrom=2020-01-01&invoiceDateTo=2030-01-01&from=0&count=100"
Write-Host "  HTTP $($rb.S)"
$baseCnt = 0
if ($rb.S -eq 200) {
    $jb = $rb.B | ConvertFrom-Json
    $baseCnt = if ($jb.values) { $jb.values.Count } else { 0 }
    Write-Host "  count=$baseCnt"
    if ($baseCnt -gt 0) { $jb.values | ForEach-Object { Write-Host "  id=$($_.id) inv=$($_.invoiceNumber) voucherId=$($_.voucher.id)" } }
}
else { Write-Host "  $($rb.B.Substring(0,[Math]::Min(200,$rb.B.Length)))" }

$rfa0 = ApiGet "/supplierInvoice/forApproval?from=0&count=100"
Write-Host "[BASELINE] GET /supplierInvoice/forApproval HTTP $($rfa0.S)"
$baseFA = 0
if ($rfa0.S -eq 200) {
    $jfa0 = $rfa0.B | ConvertFrom-Json
    $baseFA = if ($jfa0.values) { $jfa0.values.Count } else { 0 }
    Write-Host "  count=$baseFA"
    if ($baseFA -gt 0) { $jfa0.values | ForEach-Object { Write-Host "  id=$($_.id) inv=$($_.invoiceNumber)" } }
}

# A0: importDocument
Write-Host ""
Write-Host "[A0] POST /ledger/voucher/importDocument"
$r0 = ApiPostMultipart "/ledger/voucher/importDocument"
Write-Host "  HTTP $($r0.S)"
Write-Host "  $($r0.B.Substring(0,[Math]::Min(600,$r0.B.Length)))"

$voucherId = $null
if ($r0.S -eq 201) {
    $j0 = $r0.B | ConvertFrom-Json
    if ($j0.values -and $j0.values.Count -gt 0) {
        $voucherId = $j0.values[0].id
        Write-Host "  OK voucherId=$voucherId type=$($j0.values[0].voucherType.name) date=$($j0.values[0].date)"
    }
}
if ($null -eq $voucherId) {
    Write-Host "  importDocument failed - no voucherId got"
    $client.Dispose()
    exit 1
}

# A1: GET /supplierInvoice?voucherId=...
Write-Host ""
Write-Host "[A1] GET /supplierInvoice?voucherId=$voucherId"
$r1 = ApiGet "/supplierInvoice?invoiceDateFrom=2020-01-01&invoiceDateTo=2030-01-01&voucherId=$voucherId&from=0&count=100"
Write-Host "  HTTP $($r1.S)"
if ($r1.S -eq 200) {
    $j1 = $r1.B | ConvertFrom-Json
    $cnt1 = if ($j1.values) { $j1.values.Count } else { 0 }
    Write-Host "  count=$cnt1"
    if ($cnt1 -gt 0) {
        Write-Host "  *** SUPPLIER INVOICE FOUND FOR VOUCHER!"
        $j1.values | ForEach-Object { Write-Host "  *** id=$($_.id) inv=$($_.invoiceNumber) amount=$($_.amount)" }
    }
}
else { Write-Host "  $($r1.B.Substring(0,[Math]::Min(300,$r1.B.Length)))" }

Write-Host ""
Write-Host "[A1b] GET /supplierInvoice broad (baseline=$baseCnt)"
$r1b = ApiGet "/supplierInvoice?invoiceDateFrom=2020-01-01&invoiceDateTo=2030-01-01&from=0&count=100"
Write-Host "  HTTP $($r1b.S)"
if ($r1b.S -eq 200) {
    $j1b = $r1b.B | ConvertFrom-Json
    $cnt1b = if ($j1b.values) { $j1b.values.Count } else { 0 }
    Write-Host "  count=$cnt1b (delta=$(($cnt1b - $baseCnt)))"
    if ($cnt1b -gt 0) { $j1b.values | ForEach-Object { Write-Host "  id=$($_.id) inv=$($_.invoiceNumber) voucherId=$($_.voucher.id) amount=$($_.amount)" } }
}

# A2: forApproval
Write-Host ""
Write-Host "[A2] GET /supplierInvoice/forApproval (baseline=$baseFA)"
$r2 = ApiGet "/supplierInvoice/forApproval?from=0&count=100"
Write-Host "  HTTP $($r2.S)"
if ($r2.S -eq 200) {
    $j2 = $r2.B | ConvertFrom-Json
    $cnt2 = if ($j2.values) { $j2.values.Count } else { 0 }
    Write-Host "  count=$cnt2"
    if ($cnt2 -gt 0) { $j2.values | ForEach-Object { Write-Host "  * id=$($_.id) inv=$($_.invoiceNumber) supplier=$($_.supplier.name)" } }
}

# A3: GET /incomingInvoice/{voucherId}
Write-Host ""
Write-Host "[A3] GET /incomingInvoice/$voucherId"
$r3 = ApiGet "/incomingInvoice/$voucherId"
Write-Host "  HTTP $($r3.S)"
Write-Host "  $($r3.B.Substring(0,[Math]::Min(400,$r3.B.Length)))"

Write-Host ""
Write-Host "[A3b] GET /incomingInvoice (list)"
$r3b = ApiGet "/incomingInvoice?from=0&count=100"
Write-Host "  HTTP $($r3b.S)"
if ($r3b.S -eq 200) {
    $j3b = $r3b.B | ConvertFrom-Json
    $cnt3b = if ($j3b.values) { $j3b.values.Count } else { 0 }
    Write-Host "  count=$cnt3b"
    if ($cnt3b -gt 0) {
        $j3b.values | Select-Object -First 10 | ForEach-Object { Write-Host "  * id=$($_.id) voucherId=$($_.voucher.id)" }
    }
}
else { Write-Host "  $($r3b.B.Substring(0,[Math]::Min(300,$r3b.B.Length)))" }

# A4: Inspect the voucher
Write-Host ""
Write-Host "[A4] GET /ledger/voucher/$voucherId"
$r4 = ApiGet "/ledger/voucher/$voucherId"
Write-Host "  HTTP $($r4.S)"
if ($r4.S -eq 200) {
    $j4 = ($r4.B | ConvertFrom-Json).value
    Write-Host "  id=$($j4.id) type.name='$($j4.voucherType.name)' type.id=$($j4.voucherType.id)"
    Write-Host "  date=$($j4.date) description='$($j4.description)'"
    Write-Host "  postings=$($j4.postings.Count)  isDraft=$($j4.isDraft)  isApproved=$($j4.isApproved)"
}

# A5: All voucher types
Write-Host ""
Write-Host "[A5] GET /ledger/voucherType"
$r5 = ApiGet "/ledger/voucherType?from=0&count=50"
Write-Host "  HTTP $($r5.S)"
if ($r5.S -eq 200) {
    $j5 = $r5.B | ConvertFrom-Json
    $j5.values | ForEach-Object { Write-Host "  id=$($_.id) name='$($_.name)'" }
}

# A6: POST /incomingInvoice
Write-Host ""
Write-Host "[A6] POST /incomingInvoice (expected 403, but testing)"
$rSup = ApiPostJson "/supplier" '{"name":"Phase-A-Supplier","organizationNumber":"914994912"}'
Write-Host "  Supplier HTTP $($rSup.S)"
$supplierId = $null
if ($rSup.S -eq 201) { $supplierId = ($rSup.B | ConvertFrom-Json).value.id; Write-Host "  supplierId=$supplierId" }

$rAcct = ApiGet "/ledger/account?number=6500&from=0&count=1&fields=id,number"
$accountId = $null
if ($rAcct.S -eq 200) { $ja = $rAcct.B | ConvertFrom-Json; if ($ja.values.Count -gt 0) { $accountId = $ja.values[0].id }; Write-Host "  accountId=$accountId" }

$rVat = ApiGet "/ledger/vatType?number=1&count=1&fields=id"
$vatId = $null
if ($rVat.S -eq 200) { $jv = $rVat.B | ConvertFrom-Json; if ($jv.values.Count -gt 0) { $vatId = $jv.values[0].id }; Write-Host "  vatTypeId=$vatId" }

if ($supplierId) {
    $dueDate = (Get-Date).AddDays(30).ToString("yyyy-MM-dd")
    $iiBody = '{"invoiceHeader":{"vendorId":' + $supplierId + ',"invoiceDate":"' + $today + '","dueDate":"' + $dueDate + '","invoiceAmount":10000,"description":"Phase-A test","invoiceNumber":"PHASE-A-001"}}'
    $r6 = ApiPostJson "/incomingInvoice" $iiBody
    Write-Host "  POST /incomingInvoice HTTP $($r6.S)"
    Write-Host "  $($r6.B.Substring(0,[Math]::Min(600,$r6.B.Length)))"

    if ($r6.S -eq 200 -or $r6.S -eq 201) {
        Write-Host "  *** POST /incomingInvoice SUCCESS!"
    }
    else {
        Write-Host ""
        Write-Host "[A6b] PUT /incomingInvoice/$voucherId (upgrade imported voucher)"
        $putUrl = "/incomingInvoice/" + $voucherId
        $r6b = ApiPutJson $putUrl $iiBody
        Write-Host "  PUT /incomingInvoice/$voucherId HTTP $($r6b.S)"
        Write-Host "  $($r6b.B.Substring(0,[Math]::Min(600,$r6b.B.Length)))"
        if ($r6b.S -eq 200 -or $r6b.S -eq 201) {
            Write-Host "  *** PUT /incomingInvoice SUCCESS!"
        }
    }
}

$client.Dispose()

Write-Host ""
Write-Host "=================================================="
Write-Host "  PHASE A COMPLETE"
Write-Host "=================================================="
