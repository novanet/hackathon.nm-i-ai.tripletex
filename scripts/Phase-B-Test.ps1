<#
.SYNOPSIS
    Phases B-G: Exhaustive supplier invoice creation path investigation.
    Tests every plausible way to create/surface a SupplierInvoice entity.
    No app code is modified — sandbox API only.
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

$siRange = "invoiceDateFrom=2020-01-01&invoiceDateTo=2030-01-01&from=0&count=100"
$today = (Get-Date).ToString("yyyy-MM-dd")

function CheckSI($label, $voucherId) {
    $q = if ($voucherId) { "$siRange&voucherId=$voucherId" } else { $siRange }
    $r = ApiGet "/supplierInvoice?$q"
    $cnt = 0
    try { $cnt = ($r.B | ConvertFrom-Json).fullResultSize } catch {}
    Write-Host "    -> [$label] GET /supplierInvoice  HTTP=$($r.S) count=$cnt"
    return $cnt
}

Write-Host "=========================================================="
Write-Host "  PHASES B-G: Supplier Invoice exhaustive path testing"
Write-Host "  BaseUrl: $BaseUrl"
Write-Host "=========================================================="

# ─────────────────────────────────────────────────────────────
# SETUP: Look up reference data (accounts + VAT types)
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[SETUP] Looking up account 6500 (Kontorkostnader/expenses)..."
$r6500 = ApiGet "/ledger/account?number=6500&from=0&count=10"
$acct6500Id = $null
try {
    $accts = ($r6500.B | ConvertFrom-Json).values
    $acct6500Id = ($accts | Where-Object { $_.number -eq 6500 } | Select-Object -First 1).id
}
catch {}
Write-Host "  HTTP=$($r6500.S) account6500.id=$acct6500Id"

Write-Host "[SETUP] Looking up account 2400 (Leverandørgjeld/accounts payable)..."
$r2400 = ApiGet "/ledger/account?number=2400&from=0&count=10"
$acct2400Id = $null
try {
    $accts2 = ($r2400.B | ConvertFrom-Json).values
    $acct2400Id = ($accts2 | Where-Object { $_.number -eq 2400 } | Select-Object -First 1).id
}
catch {}
Write-Host "  HTTP=$($r2400.S) account2400.id=$acct2400Id"

# Also try 2900 (Gjeld) if 2400 not found
if (-not $acct2400Id) {
    $r2900 = ApiGet "/ledger/account?number=2900&from=0&count=10"
    try {
        $accts2900 = ($r2900.B | ConvertFrom-Json).values
        $acct2400Id = ($accts2900 | Where-Object { $_.number -eq 2900 } | Select-Object -First 1).id
        Write-Host "  Using account 2900 instead. id=$acct2400Id"
    }
    catch {}
}

Write-Host "[SETUP] Looking up input VAT type (25% ingoing)..."
$rVat = ApiGet "/ledger/vatType?from=0&count=100"
$vatTypeId = $null
try {
    $vatTypes = ($rVat.B | ConvertFrom-Json).values
    # Prefer 'Inngående mva 25%' style entries
    $vatType = $vatTypes | Where-Object { $_.percentage -eq 25 -and ($_.name -like "*inng*" -or $_.name -like "*input*" -or $_.name -like "*kjøp*") } | Select-Object -First 1
    if (-not $vatType) {
        $vatType = $vatTypes | Where-Object { $_.percentage -eq 25 } | Select-Object -First 1
    }
    $vatTypeId = $vatType.id
    Write-Host "  HTTP=$($rVat.S) vatType.id=$vatTypeId name=$($vatType.name) pct=$($vatType.percentage)"
}
catch {}

Write-Host "[SETUP] Creating a test supplier..."
$supplierBody = @"
{"name":"Fase B Test Leverandor AS","organizationNumber":"123456789","email":"supplier@test.no"}
"@
$rSup = ApiPostJson "/supplier" $supplierBody
$supplierId = $null
try { $supplierId = ($rSup.B | ConvertFrom-Json).value.id } catch {}
Write-Host "  HTTP=$($rSup.S) supplier.id=$supplierId"

# ─────────────────────────────────────────────────────────────
# PHASE B — POST /ledger/voucher with voucherType=9912091 (Leverandørfaktura)
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════════"
Write-Host "  PHASE B: POST /ledger/voucher with voucherType=9912091"
Write-Host "══════════════════════════════════════════════════════════"

$postingsB = @()
if ($acct6500Id -and $vatTypeId) {
    $postingsB += @"
{ "date": "$today", "description": "Kjop fra leverandor", "account": { "id": $acct6500Id }, "amountGross": 12500.00, "vatType": { "id": $vatTypeId } }
"@
}
if ($acct2400Id) {
    $postingsB += @"
{ "date": "$today", "description": "Leverandorgjeld", "account": { "id": $acct2400Id }, "amountGross": -12500.00 }
"@
}
$postingsStr = $postingsB -join ","

$voucherBodyB = @"
{
  "date": "$today",
  "description": "Leverandorfaktura Fase B",
  "voucherType": { "id": 9912091 },
  "postings": [$postingsStr]
}
"@
Write-Host "Posting voucher with voucherType=9912091..."
$rVB = ApiPostJson "/ledger/voucher" $voucherBodyB
Write-Host "  HTTP=$($rVB.S)"
$voucherIdB = $null
try { $voucherIdB = ($rVB.B | ConvertFrom-Json).value.id } catch {}
if ($rVB.S -ne 201 -and $rVB.S -ne 200) {
    $msgs = ""
    try { $msgs = (($rVB.B | ConvertFrom-Json).validationMessages | ForEach-Object { $_.message }) -join "; " } catch {}
    Write-Host "  ERROR: $msgs"
    Write-Host "  BODY: $($rVB.B)"
}
else {
    Write-Host "  voucher.id=$voucherIdB"
}

Start-Sleep -Milliseconds 500
$cntB = CheckSI "Phase B direct" $voucherIdB
$cntBAll = CheckSI "Phase B all" $null

# Inspect the voucher to see if voucherType was stored
if ($voucherIdB) {
    $rVI = ApiGet "/ledger/voucher/$voucherIdB"
    try {
        $vObj = ($rVI.B | ConvertFrom-Json).value
        Write-Host "  Voucher voucherType=$($vObj.voucherType | ConvertTo-Json -Compress) postings=$($vObj.postings.Count)"
    }
    catch { Write-Host "  Could not parse voucher" }
}

# ─────────────────────────────────────────────────────────────
# PHASE C — Try to enable incoming invoice module
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════════"
Write-Host "  PHASE C: Attempt module activation (/module, /company/settings)"
Write-Host "══════════════════════════════════════════════════════════"

Write-Host "GET /module..."
$rMod = ApiGet "/module?from=0&count=100"
Write-Host "  HTTP=$($rMod.S)"
$incomingModuleId = $null
if ($rMod.S -eq 200) {
    try {
        $modules = ($rMod.B | ConvertFrom-Json).values
        Write-Host "  Total modules: $($modules.Count)"
        foreach ($m in $modules) {
            $nameMatch = $m.name -like "*faktura*inn*" -or $m.name -like "*incoming*" -or $m.name -like "*leverandor*"
            if ($nameMatch) {
                Write-Host "  MATCH: id=$($m.id) name=$($m.name) isActive=$($m.isActive)"
                $incomingModuleId = $m.id
            }
        }
        # Print all modules for visibility
        Write-Host ""
        Write-Host "  All modules:"
        foreach ($m in $modules) {
            Write-Host "    id=$($m.id) name=$($m.name) isActive=$($m.isActive)"
        }
    }
    catch { Write-Host "  Could not parse modules: $($rMod.B.Substring(0, [Math]::Min(200, $rMod.B.Length)))" }
}

if ($incomingModuleId) {
    Write-Host "Attempting to activate module id=$incomingModuleId..."
    $rModGet = ApiGet "/module/$incomingModuleId"
    $modVersion = $null
    try { $modVersion = ($rModGet.B | ConvertFrom-Json).value.version } catch {}
    $modBody = "{`"id`": $incomingModuleId, `"isActive`": true, `"version`": $modVersion}"
    $rModPut = ApiPutJson "/module/$incomingModuleId" $modBody
    Write-Host "  PUT HTTP=$($rModPut.S)"
    if ($rModPut.S -eq 200) {
        Write-Host "  MODULE ACTIVATED — retesting /incomingInvoice..."
        $rII = ApiGet "/incomingInvoice?from=0&count=10"
        Write-Host "  GET /incomingInvoice HTTP=$($rII.S)"
    }
}

Write-Host "GET /company/settings (look for invoice-related flags)..."
$rCS = ApiGet "/company/settings"
Write-Host "  HTTP=$($rCS.S)"
if ($rCS.S -eq 200) {
    try {
        $cs = ($rCS.B | ConvertFrom-Json).value
        Write-Host "  Settings preview:"
        $cs.PSObject.Properties | Where-Object { $_.Name -like "*invoice*" -or $_.Name -like "*faktura*" -or $_.Name -like "*incoming*" -or $_.Name -like "*supplier*" } | ForEach-Object {
            Write-Host "    $($_.Name) = $($_.Value)"
        }
    }
    catch {}
}

# ─────────────────────────────────────────────────────────────
# PHASE D — importDocument -> sendToLedger
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════════"
Write-Host "  PHASE D: importDocument -> :sendToLedger"
Write-Host "══════════════════════════════════════════════════════════"

Write-Host "POST /ledger/voucher/importDocument..."
$rImp = ApiPostMultipart "/ledger/voucher/importDocument"
$impVoucherId = $null
if ($rImp.S -eq 201 -or $rImp.S -eq 200) {
    try { $impVoucherId = ($rImp.B | ConvertFrom-Json).values[0].id } catch {}
    Write-Host "  HTTP=$($rImp.S) voucherId=$impVoucherId"
}
else {
    Write-Host "  HTTP=$($rImp.S) BODY=$($rImp.B.Substring(0, [Math]::Min(200, $rImp.B.Length)))"
}

if ($impVoucherId) {
    Write-Host "PUT /ledger/voucher/$impVoucherId/:sendToLedger..."
    $rSTL = ApiPutJson "/ledger/voucher/$impVoucherId/:sendToLedger" "{}"
    Write-Host "  HTTP=$($rSTL.S)"
    if ($rSTL.S -ne 200) {
        $msgs2 = ""
        try { $msgs2 = (($rSTL.B | ConvertFrom-Json).validationMessages | ForEach-Object { $_.message }) -join "; " } catch {}
        Write-Host "  ERROR: $msgs2"
        Write-Host "  BODY: $($rSTL.B.Substring(0, [Math]::Min(300, $rSTL.B.Length)))"
    }
    Start-Sleep -Milliseconds 500
    CheckSI "Phase D" $impVoucherId | Out-Null
}

# ─────────────────────────────────────────────────────────────
# PHASE E — Does POST /supplierInvoice exist at all?
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════════"
Write-Host "  PHASE E: Does POST /supplierInvoice exist?"
Write-Host "══════════════════════════════════════════════════════════"

$siBody = @"
{
  "invoiceDate": "$today",
  "dueDate": "$today",
  "amount": 12500.00,
  "currency": { "code": "NOK" }
}
"@
$rSIPost = ApiPostJson "/supplierInvoice" $siBody
Write-Host "  POST /supplierInvoice HTTP=$($rSIPost.S)"
$siMsg = ""
try { $siMsg = (($rSIPost.B | ConvertFrom-Json).validationMessages | ForEach-Object { $_.message }) -join "; " } catch {}
Write-Host "  validationMessages: $siMsg"
Write-Host "  BODY: $($rSIPost.B.Substring(0, [Math]::Min(400, $rSIPost.B.Length)))"

# Also try POST /supplierInvoice with supplier link
if ($supplierId) {
    $siBody2 = @"
{
  "invoiceDate": "$today",
  "dueDate": "$today",
  "amount": 12500.00,
  "currency": { "code": "NOK" },
  "supplier": { "id": $supplierId }
}
"@
    $rSIPost2 = ApiPostJson "/supplierInvoice" $siBody2
    Write-Host "  POST /supplierInvoice (with supplier) HTTP=$($rSIPost2.S)"
    $siMsg2 = ""
    try { $siMsg2 = (($rSIPost2.B | ConvertFrom-Json).validationMessages | ForEach-Object { $_.message }) -join "; " } catch {}
    Write-Host "  validationMessages: $siMsg2"
}

# ─────────────────────────────────────────────────────────────
# PHASE F — Check Voucher schema for vendor / supplierInvoice fields
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════════"
Write-Host "  PHASE F: POST /ledger/voucher with vendor/supplier fields"
Write-Host "══════════════════════════════════════════════════════════"

# Try including a supplier reference on the voucher
$voucherBodyF = @"
{
  "date": "$today",
  "description": "Leverandorfaktura med leverandor-ref",
  "voucherType": { "id": 9912091 },
  "supplier": { "id": $supplierId },
  "postings": [$postingsStr]
}
"@
Write-Host "Posting voucher with supplier reference..."
$rVF = ApiPostJson "/ledger/voucher" $voucherBodyF
Write-Host "  HTTP=$($rVF.S)"
$voucherIdF = $null
try { $voucherIdF = ($rVF.B | ConvertFrom-Json).value.id } catch {}
if ($rVF.S -ne 201 -and $rVF.S -ne 200) {
    $msgs3 = ""
    try { $msgs3 = (($rVF.B | ConvertFrom-Json).validationMessages | ForEach-Object { $_.message }) -join "; " } catch {}
    Write-Host "  ERROR: $msgs3"
}
else {
    Write-Host "  voucher.id=$voucherIdF"
    Start-Sleep -Milliseconds 500
    CheckSI "Phase F" $voucherIdF | Out-Null
    # Can we query by supplierId?
    $rSIF = ApiGet "/supplierInvoice?$siRange&supplierId=$supplierId"
    Write-Host "  GET /supplierInvoice?supplierId=$supplierId HTTP=$($rSIF.S)"
    try { Write-Host "  count=$((($rSIF.B | ConvertFrom-Json).fullResultSize))" } catch {}
}

# Try with vendorInvoiceNumber field
$voucherBodyF2 = @"
{
  "date": "$today",
  "description": "Leverandorfaktura med nummer",
  "voucherType": { "id": 9912091 },
  "vendorInvoiceNumber": "SINV-2026-001",
  "postings": [$postingsStr]
}
"@
Write-Host "Posting voucher with vendorInvoiceNumber..."
$rVF2 = ApiPostJson "/ledger/voucher" $voucherBodyF2
Write-Host "  HTTP=$($rVF2.S) (vendorInvoiceNumber field)"
if ($rVF2.S -eq 201 -or $rVF2.S -eq 200) {
    $voucherIdF2 = $null
    try { $voucherIdF2 = ($rVF2.B | ConvertFrom-Json).value.id } catch {}
    Write-Host "  voucher.id=$voucherIdF2"
    # Inspect the created voucher
    if ($voucherIdF2) {
        $rVF2Get = ApiGet "/ledger/voucher/$voucherIdF2"
        try {
            $vObjF2 = ($rVF2Get.B | ConvertFrom-Json).value
            Write-Host "  vendorInvoiceNumber=$($vObjF2.vendorInvoiceNumber) voucherType=$($vObjF2.voucherType | ConvertTo-Json -Compress)"
        }
        catch {}
        CheckSI "Phase F2" $voucherIdF2 | Out-Null
    }
}

# ─────────────────────────────────────────────────────────────
# PHASE G — Change existing plain voucher's type to Leverandørfaktura via PUT
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════════"
Write-Host "  PHASE G: POST plain voucher -> PUT with voucherType=9912091"
Write-Host "══════════════════════════════════════════════════════════"

$plainVoucherBody = @"
{
  "date": "$today",
  "description": "Plain voucher pre-type-change",
  "postings": [$postingsStr]
}
"@
Write-Host "Creating plain voucher..."
$rVG = ApiPostJson "/ledger/voucher" $plainVoucherBody
Write-Host "  HTTP=$($rVG.S)"
$voucherIdG = $null
$versionG = $null
try {
    $vObjG = ($rVG.B | ConvertFrom-Json).value
    $voucherIdG = $vObjG.id
    $versionG = $vObjG.version
}
catch {}

if ($voucherIdG) {
    Write-Host "  plain voucher.id=$voucherIdG version=$versionG"
    Write-Host "  Updating voucherType to Leverandørfaktura (9912091)..."
    $putBodyG = @"
{
  "id": $voucherIdG,
  "version": $versionG,
  "date": "$today",
  "description": "Plain voucher pre-type-change",
  "voucherType": { "id": 9912091 },
  "postings": [$postingsStr]
}
"@
    $rVGPut = ApiPutJson "/ledger/voucher/$voucherIdG" $putBodyG
    Write-Host "  PUT HTTP=$($rVGPut.S)"
    if ($rVGPut.S -ne 200) {
        $msgs4 = ""
        try { $msgs4 = (($rVGPut.B | ConvertFrom-Json).validationMessages | ForEach-Object { $_.message }) -join "; " } catch {}
        Write-Host "  ERROR: $msgs4"
    }
    Start-Sleep -Milliseconds 500
    CheckSI "Phase G" $voucherIdG | Out-Null
}

# ─────────────────────────────────────────────────────────────
# PHASE H — Check /supplierInvoice with all the IDs we created
# ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════════"
Write-Host "  FINAL SWEEP: Broad /supplierInvoice search"
Write-Host "══════════════════════════════════════════════════════════"
$rFinal = ApiGet "/supplierInvoice?$siRange"
Write-Host "  HTTP=$($rFinal.S)"
try {
    $finalObj = $rFinal.B | ConvertFrom-Json
    Write-Host "  fullResultSize=$($finalObj.fullResultSize)  count=$($finalObj.values.Count)"
    foreach ($si in $finalObj.values) {
        Write-Host "  FOUND SI: id=$($si.id) voucherId=$($si.voucher.id) supplier=$($si.supplier.name)"
    }
}
catch { Write-Host "  Parse error: $($rFinal.B.Substring(0, [Math]::Min(300, $rFinal.B.Length)))" }

Write-Host ""
Write-Host "=================================================================="
Write-Host "  SUMMARY"
Write-Host "=================================================================="
Write-Host "  Phase B (voucher+voucherType=9912091):   SI count = $cntBAll"
Write-Host "  Phase E: POST /supplierInvoice HTTP code = $($rSIPost.S)"
Write-Host ""
Write-Host "  acct6500Id=$acct6500Id  acct2400Id=$acct2400Id  vatTypeId=$vatTypeId"
Write-Host "  supplierId=$supplierId  voucherIdB=$voucherIdB"
Write-Host "=================================================================="
