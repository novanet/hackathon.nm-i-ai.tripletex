<#
.SYNOPSIS
    Systematically probes the Tripletex sandbox API to discover validation rules,
    required fields, reference data IDs, and module requirements.

.DESCRIPTION
    This script runs OUTSIDE the /solve endpoint — direct API calls to the sandbox.
    No scoring impact. Captures everything to discovery/results.json.

    Phases:
    1. Reference Data Snapshot — capture all reference IDs on a fresh account
    2. Entity Creation Probes — POST with minimal fields, read validationMessages
    3. Action Endpoint Discovery — probe :action endpoints
    4. Module Activation Probes — check which modules are enabled/needed
#>
param(
    [string]$BaseUrl,
    [string]$SessionToken
)

$ErrorActionPreference = "Continue"

# --- Load credentials from user-secrets if not provided ---
if (-not $BaseUrl -or -not $SessionToken) {
    $secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
    if ($secretsJson) {
        foreach ($line in $secretsJson) {
            if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$' -and -not $BaseUrl) {
                $BaseUrl = $Matches[1].Trim()
            }
            if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$' -and -not $SessionToken) {
                $SessionToken = $Matches[1].Trim()
            }
        }
    }
}

if (-not $BaseUrl -or -not $SessionToken) {
    Write-Error "Missing credentials. Pass -BaseUrl and -SessionToken or configure user-secrets."
    return
}

Write-Host "=== Tripletex API Discovery ===" -ForegroundColor Cyan
Write-Host "Base URL: $BaseUrl"
Write-Host ""

# --- Auth header ---
$authPair = "0:$SessionToken"
$authBytes = [System.Text.Encoding]::ASCII.GetBytes($authPair)
$authB64 = [System.Convert]::ToBase64String($authBytes)
$headers = @{ "Authorization" = "Basic $authB64" }

# --- Output structure ---
$discovery = @{
    timestamp      = (Get-Date -Format "o")
    base_url       = $BaseUrl
    reference_data = @{}
    entity_probes  = @{}
    action_probes  = @{}
    module_info    = @{}
    call_count     = 0
    error_count    = 0
}

# --- Helper: Make API call and capture result ---
function Invoke-TxApi {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [switch]$SuppressError
    )

    $url = "$BaseUrl$Path"
    $discovery.call_count++

    $params = @{
        Method             = $Method
        Uri                = $url
        Headers            = $headers
        ContentType        = "application/json; charset=utf-8"
        SkipHttpErrorCheck = $true   # PS7: don't throw on 4xx/5xx
    }

    if ($Body) {
        $jsonBody = $Body | ConvertTo-Json -Depth 10 -Compress
        $params["Body"] = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
    }

    $response = Invoke-WebRequest @params -ErrorAction Stop
    $statusCode = [int]$response.StatusCode
    $responseBody = $null
    if ($response.Content) {
        try { $responseBody = $response.Content | ConvertFrom-Json } catch { }
    }

    if ($statusCode -ge 200 -and $statusCode -lt 300) {
        return @{ status = $statusCode; data = $responseBody; error = $null }
    }

    # Error path
    $discovery.error_count++
    $validationMsgs = ""
    if ($responseBody -and $responseBody.validationMessages) {
        $validationMsgs = ($responseBody.validationMessages | ForEach-Object { "$($_.field): $($_.message)" }) -join "; "
    }
    $devMsg = if ($responseBody -and $responseBody.developerMessage) { $responseBody.developerMessage } else { "HTTP $statusCode" }

    if (-not $SuppressError) {
        Write-Host "  [$statusCode] $devMsg" -ForegroundColor Yellow
        if ($validationMsgs) { Write-Host "  Validation: $validationMsgs" -ForegroundColor Yellow }
    }

    return @{
        status             = $statusCode
        data               = $null
        error              = $responseBody
        errorMessage       = $devMsg
        validationMessages = if ($responseBody -and $responseBody.validationMessages) { $responseBody.validationMessages } else { @() }
    }
}

# --- Helper: Delete entity we just created (cleanup) ---
function Remove-TxEntity {
    param([string]$Path, [long]$Id)
    if ($Id -gt 0) {
        Invoke-TxApi -Method "DELETE" -Path "$Path/$Id" -SuppressError | Out-Null
    }
}

# ============================================================
# PHASE 1: Reference Data Snapshot
# ============================================================
Write-Host "`n=== PHASE 1: Reference Data Snapshot ===" -ForegroundColor Green

$refEndpoints = @{
    "vatTypes"              = "/ledger/vatType?count=100&fields=*"
    "invoicePaymentTypes"   = "/invoice/paymentType?count=100&fields=*"
    "travelPaymentTypes"    = "/travelExpense/paymentType?count=100&fields=*"
    "travelCostCategories"  = "/travelExpense/costCategory?count=100&fields=*"
    "accounts"              = "/ledger/account?count=2000&fields=id,number,name"
    "salesModules"          = "/company/salesmodules?count=100&fields=*"
    "voucherTypes"          = "/ledger/voucherType?count=100&fields=*"
    "employeeCategories"    = "/employee/category?count=100&fields=*"
    "customerCategories"    = "/customer/category?count=100&fields=*"
    "projectCategories"     = "/project/category?count=100&fields=*"
    "currencies"            = "/currency?count=100&fields=*"
    "countries"             = "/country?name=Norge&count=5&fields=id,name"
    "productUnits"          = "/product/unit?count=100&fields=*"
    "employmentTypes"       = "/employee/employment/employmentType?count=100&fields=*"
    "remunerationTypes"     = "/employee/employment/remunerationType?count=100&fields=*"
    "workingHoursSchemes"   = "/employee/employment/workingHoursScheme?count=100&fields=*"
    "leaveOfAbsenceTypes"   = "/employee/employment/leaveOfAbsenceType?count=100&fields=*"
    "occupationCodes"       = "/employee/employment/occupationCode?count=20&fields=*"
    "activities"            = "/activity?count=100&fields=*"
    "ledgerPaymentTypesOut" = "/ledger/paymentTypeOut?count=100&fields=*"
}

foreach ($key in $refEndpoints.Keys) {
    Write-Host "  Fetching $key..." -NoNewline
    $result = Invoke-TxApi -Method "GET" -Path $refEndpoints[$key]
    if ($result.status -eq 200 -and $result.data) {
        $values = if ($result.data.values) { $result.data.values } elseif ($result.data.value) { @($result.data.value) } else { $result.data }
        $count = if ($values -is [array]) { $values.Count } else { 1 }
        Write-Host " $count items" -ForegroundColor Green
        $discovery.reference_data[$key] = $values
    }
    else {
        Write-Host " FAILED" -ForegroundColor Red
        $discovery.reference_data[$key] = $null
    }
}

# ============================================================
# PHASE 2: Entity Creation Probes
# ============================================================
Write-Host "`n=== PHASE 2: Entity Creation Probes ===" -ForegroundColor Green

# Each probe: attempt POST with minimal body, capture validation, then try with more fields
$entityProbes = [ordered]@{
    # --- Already covered by handlers, but validate ---
    "employee_minimal"                   = @{
        path    = "/employee"
        body    = @{}
        cleanup = "/employee"
    }
    "employee_names_only"                = @{
        path    = "/employee"
        body    = @{ firstName = "TestDisc"; lastName = "OveryBot" }
        cleanup = "/employee"
    }
    "employee_full"                      = @{
        path    = "/employee"
        body    = @{
            firstName         = "TestDisc"
            lastName          = "OveryBot"
            email             = "testdiscovery@example.org"
            userType          = "STANDARD"
            dateOfBirth       = "1990-01-01"
            phoneNumberMobile = "+47 99999999"
        }
        cleanup = "/employee"
    }
    "customer_minimal"                   = @{
        path    = "/customer"
        body    = @{}
        cleanup = "/customer"
    }
    "customer_name_only"                 = @{
        path    = "/customer"
        body    = @{ name = "DiscoveryTest AS"; isCustomer = $true }
        cleanup = "/customer"
    }
    "product_minimal"                    = @{
        path    = "/product"
        body    = @{}
        cleanup = "/product"
    }
    "product_name_only"                  = @{
        path    = "/product"
        body    = @{ name = "DiscoveryProduct" }
        cleanup = "/product"
    }
    "department_minimal"                 = @{
        path    = "/department"
        body    = @{}
        cleanup = "/department"
    }
    "department_name"                    = @{
        path    = "/department"
        body    = @{ name = "DiscoveryDept" }
        cleanup = "/department"
    }
    "supplier_minimal"                   = @{
        path    = "/supplier"
        body    = @{}
        cleanup = "/supplier"
    }
    "supplier_name"                      = @{
        path    = "/supplier"
        body    = @{ name = "DiscoverySupplier AS" }
        cleanup = "/supplier"
    }

    # --- Not yet covered — discover required fields ---
    "contact_minimal"                    = @{
        path    = "/contact"
        body    = @{}
        cleanup = "/contact"
    }
    "contact_with_fields"                = @{
        path    = "/contact"
        body    = @{ firstName = "Contact"; lastName = "Person" }
        cleanup = "/contact"
    }
    "employment_minimal"                 = @{
        path    = "/employee/employment"
        body    = @{}
        cleanup = $null  # don't delete
    }
    "nextOfKin_minimal"                  = @{
        path    = "/employee/nextOfKin"
        body    = @{}
        cleanup = $null
    }
    "purchaseOrder_minimal"              = @{
        path    = "/purchaseOrder"
        body    = @{}
        cleanup = "/purchaseOrder"
    }
    "supplierInvoice_minimal"            = @{
        path    = "/supplier/invoice"
        body    = @{}
        cleanup = $null
    }
    "salaryTransaction_minimal"          = @{
        path    = "/salary/transaction"
        body    = @{}
        cleanup = $null
    }
    "salaryPayslip_minimal"              = @{
        path    = "/salary/payslip"
        body    = @{}
        cleanup = $null
    }
    "timesheetEntry_minimal"             = @{
        path    = "/timesheet/entry"
        body    = @{}
        cleanup = "/timesheet/entry"
    }
    "activity_minimal"                   = @{
        path    = "/activity"
        body    = @{}
        cleanup = "/activity"
    }
    "activity_name"                      = @{
        path    = "/activity"
        body    = @{ name = "DiscoveryActivity"; number = "DA001" }
        cleanup = "/activity"
    }
    "bankReconciliation_minimal"         = @{
        path    = "/bank/reconciliation"
        body    = @{}
        cleanup = $null
    }
    "reminder_minimal"                   = @{
        path    = "/reminder"
        body    = @{}
        cleanup = $null
    }
    "resultBudget_minimal"               = @{
        path    = "/resultbudget"
        body    = @{}
        cleanup = $null
    }
    "voucherImportDoc_minimal"           = @{
        path    = "/ledger/voucher/importDocument"
        body    = @{}
        cleanup = $null
    }
    "division_minimal"                   = @{
        path    = "/division"
        body    = @{}
        cleanup = "/division"
    }
    "division_name"                      = @{
        path    = "/division"
        body    = @{ name = "DiscoveryDiv" }
        cleanup = "/division"
    }
    "inventory_minimal"                  = @{
        path    = "/inventory"
        body    = @{}
        cleanup = $null
    }
    "inventoryLocation_minimal"          = @{
        path    = "/inventory/location"
        body    = @{}
        cleanup = $null
    }
    "orderGroup_minimal"                 = @{
        path    = "/order/orderGroup"
        body    = @{}
        cleanup = $null
    }
    "projectTask_minimal"                = @{
        path    = "/project/task"
        body    = @{}
        cleanup = $null
    }
    "projectParticipant_minimal"         = @{
        path    = "/project/participant"
        body    = @{}
        cleanup = $null
    }
    "projectActivity_minimal"            = @{
        path    = "/project/projectActivity"
        body    = @{}
        cleanup = $null
    }
    "ledgerAccount_minimal"              = @{
        path    = "/ledger/account"
        body    = @{}
        cleanup = $null
    }
    "ledgerVoucher_minimal"              = @{
        path    = "/ledger/voucher"
        body    = @{}
        cleanup = $null
    }
    "ledgerVoucher_basic"                = @{
        path    = "/ledger/voucher"
        body    = @{ date = "2026-03-20"; description = "Discovery test" }
        cleanup = $null
    }
    "travelExpense_minimal"              = @{
        path    = "/travelExpense"
        body    = @{}
        cleanup = "/travelExpense"
    }
    "travelExpenseCost_minimal"          = @{
        path    = "/travelExpense/cost"
        body    = @{}
        cleanup = $null
    }
    "travelExpenseMileage_minimal"       = @{
        path    = "/travelExpense/mileage_allowance"
        body    = @{}
        cleanup = $null
    }
    "travelExpensePerDiem_minimal"       = @{
        path    = "/travelExpense/perDiemCompensation"
        body    = @{}
        cleanup = $null
    }
    "travelExpenseAccommodation_minimal" = @{
        path    = "/travelExpense/accommodation_allowance"
        body    = @{}
        cleanup = $null
    }
    "salaryType_minimal"                 = @{
        path    = "/salary/type"
        body    = @{}
        cleanup = $null
    }
    "salarySettings_minimal"             = @{
        path    = "/salary/settings"
        body    = @{}
        cleanup = $null
    }
    "productUnit_minimal"                = @{
        path    = "/product/unit"
        body    = @{}
        cleanup = $null
    }
    "productUnit_name"                   = @{
        path    = "/product/unit"
        body    = @{ name = "DiscUnit"; nameShort = "DU" }
        cleanup = "/product/unit"
    }
    "voucherMessage_minimal"             = @{
        path    = "/voucherMessage"
        body    = @{}
        cleanup = $null
    }
    "invoicePayment_minimal"             = @{
        path    = "/invoice/payment"
        body    = @{}
        cleanup = $null
    }
    "bankReconcPaymentType_minimal"      = @{
        path    = "/bank/reconciliation/paymentType"
        body    = @{}
        cleanup = $null
    }
    "eventSubscription_minimal"          = @{
        path    = "/event/subscription"
        body    = @{}
        cleanup = $null
    }
}

$createdIds = @{}  # track IDs for cleanup

foreach ($probeName in $entityProbes.Keys) {
    $probe = $entityProbes[$probeName]
    Write-Host "`n  Probing: $probeName → POST $($probe.path)" -ForegroundColor Cyan

    $result = Invoke-TxApi -Method "POST" -Path $probe.path -Body $probe.body

    $probeResult = @{
        path               = $probe.path
        bodyUsed           = $probe.body
        status             = $result.status
        validationMessages = $result.validationMessages
        errorMessage       = $result.errorMessage
        success            = ($result.status -ge 200 -and $result.status -lt 300)
    }

    if ($probeResult.success -and $result.data -and $result.data.value) {
        $createdId = $result.data.value.id
        $probeResult["createdId"] = $createdId
        $probeResult["responseFields"] = ($result.data.value | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name)
        Write-Host "  SUCCESS (id=$createdId)" -ForegroundColor Green

        # Cleanup
        if ($probe.cleanup -and $createdId) {
            Remove-TxEntity -Path $probe.cleanup -Id $createdId
        }
    }

    $discovery.entity_probes[$probeName] = $probeResult
}

# ============================================================
# PHASE 3: Action Endpoint Discovery
# ============================================================
Write-Host "`n=== PHASE 3: Action Endpoints ===" -ForegroundColor Green

$actionEndpoints = @(
    @{ method = "PUT"; path = "/invoice/{id}/:send"; note = "Send invoice (need invoice ID)" }
    @{ method = "PUT"; path = "/invoice/{id}/:createCreditNote"; note = "Create credit note" }
    @{ method = "PUT"; path = "/invoice/{id}/:createReminder"; note = "Create payment reminder" }
    @{ method = "PUT"; path = "/invoice/{id}/:payment"; note = "Register payment" }
    @{ method = "PUT"; path = "/ledger/voucher/{id}/:reverse"; note = "Reverse voucher" }
    @{ method = "PUT"; path = "/ledger/voucher/{id}/:sendToInbox"; note = "Send voucher to inbox" }
    @{ method = "PUT"; path = "/ledger/voucher/{id}/:sendToLedger"; note = "Send voucher to ledger" }
    @{ method = "PUT"; path = "/order/{id}/:invoice"; note = "Create invoice from order" }
    @{ method = "PUT"; path = "/order/:invoiceMultipleOrders"; note = "Invoice multiple orders" }
    @{ method = "PUT"; path = "/employee/entitlement/:grantEntitlementsByTemplate"; note = "Grant employee entitlements" }
    @{ method = "PUT"; path = "/employee/entitlement/:grantClientEntitlementsByTemplate"; note = "Grant client entitlements" }
    @{ method = "PUT"; path = "/employee/preferences/:changeLanguage"; note = "Change employee language" }
    @{ method = "PUT"; path = "/travelExpense/:deliver"; note = "Submit travel expense" }
    @{ method = "PUT"; path = "/travelExpense/:approve"; note = "Approve travel expense" }
    @{ method = "PUT"; path = "/travelExpense/:unapprove"; note = "Unapprove travel expense" }
    @{ method = "PUT"; path = "/travelExpense/:createVouchers"; note = "Create vouchers from travel" }
    @{ method = "PUT"; path = "/purchaseOrder/{id}/:send"; note = "Send purchase order" }
    @{ method = "PUT"; path = "/purchaseOrder/{id}/:sendByEmail"; note = "Send PO by email" }
    @{ method = "PUT"; path = "/purchaseOrder/goodsReceipt/{id}/:confirm"; note = "Confirm goods receipt" }
    @{ method = "PUT"; path = "/supplierInvoice/{invoiceId}/:approve"; note = "Approve supplier invoice" }
    @{ method = "PUT"; path = "/supplierInvoice/{invoiceId}/:addPayment"; note = "Add payment to supplier invoice" }
    @{ method = "PUT"; path = "/bank/reconciliation/{id}/:adjustment"; note = "Bank reconciliation adjustment" }
    @{ method = "PUT"; path = "/timesheet/month/:approve"; note = "Approve timesheet month" }
    @{ method = "PUT"; path = "/timesheet/month/:complete"; note = "Complete timesheet month" }
    @{ method = "PUT"; path = "/timesheet/week/:approve"; note = "Approve timesheet week" }
    @{ method = "PUT"; path = "/ledger/posting/:closePostings"; note = "Close ledger postings" }
    @{ method = "PUT"; path = "/ledger/voucher/historical/:closePostings"; note = "Close historical postings" }
    @{ method = "PUT"; path = "/ledger/voucher/historical/:reverseHistoricalVouchers"; note = "Reverse historical vouchers" }
)

# Just record all known action endpoints — don't actually call them since they need entity IDs
$discovery.action_probes = $actionEndpoints
Write-Host "  Recorded $($actionEndpoints.Count) action endpoints" -ForegroundColor Green

# ============================================================
# PHASE 4: Module Probes
# ============================================================
Write-Host "`n=== PHASE 4: Module & Company Info ===" -ForegroundColor Green

# Get company info
Write-Host "  Fetching company info..." -NoNewline
$companyResult = Invoke-TxApi -Method "GET" -Path "/company?fields=*"
if ($companyResult.status -eq 200) {
    Write-Host " OK" -ForegroundColor Green
    $discovery.module_info["company"] = $companyResult.data
}

# Get current employee (logged in user)
Write-Host "  Fetching current employee (me)..." -NoNewline
$meResult = Invoke-TxApi -Method "GET" -Path "/token/session/>whoAmI?fields=*"
if ($meResult.status -eq 200) {
    Write-Host " OK" -ForegroundColor Green
    $discovery.module_info["whoAmI"] = $meResult.data
}

# Get all employees on the account
Write-Host "  Fetching all employees..." -NoNewline
$empResult = Invoke-TxApi -Method "GET" -Path "/employee?count=100&fields=id,firstName,lastName,email,userType"
if ($empResult.status -eq 200) {
    Write-Host " $($empResult.data.values.Count) employees" -ForegroundColor Green
    $discovery.module_info["employees"] = $empResult.data.values
}

# Get entitlement templates
Write-Host "  Fetching employee entitlements for first employee..." -NoNewline
if ($empResult.status -eq 200 -and $empResult.data.values.Count -gt 0) {
    $firstEmpId = $empResult.data.values[0].id
    $entResult = Invoke-TxApi -Method "GET" -Path "/employee/entitlement?employeeId=$firstEmpId&count=100&fields=*"
    if ($entResult.status -eq 200) {
        Write-Host " $($entResult.data.values.Count) entitlements" -ForegroundColor Green
        $discovery.module_info["entitlements_sample"] = $entResult.data.values
    }
}

# Check bank accounts on the company
Write-Host "  Fetching company bank accounts..." -NoNewline
$bankAccResult = Invoke-TxApi -Method "GET" -Path "/bank?count=100&fields=*" -SuppressError
if ($bankAccResult.status -eq 200) {
    $bankData = if ($bankAccResult.data.values) { $bankAccResult.data.values } else { @() }
    Write-Host " $($bankData.Count) bank accounts" -ForegroundColor Green
    $discovery.module_info["bankAccounts"] = $bankData
}
else {
    Write-Host " FAILED ($($bankAccResult.status))" -ForegroundColor Yellow
    # Try alternative path
    $bankAccResult2 = Invoke-TxApi -Method "GET" -Path "/company/settings/altinn?fields=*" -SuppressError
    $discovery.module_info["bankAccounts"] = $null
}

# ============================================================
# PHASE 5: Save Results
# ============================================================
Write-Host "`n=== Saving Results ===" -ForegroundColor Green

$outDir = Join-Path $PSScriptRoot "..\discovery"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$outPath = Join-Path $outDir "results.json"
$discovery | ConvertTo-Json -Depth 15 | Set-Content -Path $outPath -Encoding UTF8
Write-Host "  Saved to: $outPath" -ForegroundColor Green

# ============================================================
# Summary
# ============================================================
Write-Host "`n=== SUMMARY ===" -ForegroundColor Cyan
Write-Host "  Total API calls: $($discovery.call_count)"
Write-Host "  Total errors (expected): $($discovery.error_count)"
Write-Host ""

# Categorize probes
$successes = @()
$failures = @()
foreach ($key in $discovery.entity_probes.Keys) {
    $p = $discovery.entity_probes[$key]
    if ($p.success) { $successes += "$key ($($p.path))" }
    else { $failures += "$key ($($p.path)) → $($p.errorMessage)" }
}

Write-Host "  SUCCESSFUL CREATES ($($successes.Count)):" -ForegroundColor Green
$successes | ForEach-Object { Write-Host "    $_" }

Write-Host ""
Write-Host "  REQUIRED FIELD DISCOVERIES ($($failures.Count)):" -ForegroundColor Yellow
$failures | ForEach-Object { Write-Host "    $_" }

# Print reference data summary
Write-Host ""
Write-Host "  REFERENCE DATA:" -ForegroundColor Green
foreach ($key in $discovery.reference_data.Keys) {
    $vals = $discovery.reference_data[$key]
    $count = if ($vals -is [array]) { $vals.Count } else { "N/A" }
    Write-Host "    $key : $count items"
}

Write-Host "`n=== Discovery Complete ===" -ForegroundColor Cyan
