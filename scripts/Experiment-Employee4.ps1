<#
.SYNOPSIS
    Follow-up: Test inline employments WITH email, and other edge cases
#>
$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $Token = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $Token = $Matches[1].Trim() }
}

$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$Token"))
$headers = @{ "Authorization" = "Basic $cred"; "Content-Type" = "application/json" }

function Api-Call($method, $path, $bodyObj = $null, $params = @{}) {
    $qs = ($params.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "&"
    $url = if ($qs) { "$BaseUrl$path`?$qs" } else { "$BaseUrl$path" }
    $splat = @{ Uri = $url; Headers = $headers; Method = $method; SkipHttpErrorCheck = $true; StatusCodeVariable = 'sc' }
    if ($bodyObj) { $splat['Body'] = ($bodyObj | ConvertTo-Json -Depth 5) }
    $response = Invoke-RestMethod @splat
    $code = $sc
    if ($code -ge 200 -and $code -lt 300) { return @{ success = $true; code = $code; data = $response } }
    else { return @{ success = $false; code = $code; data = $response } }
}

# Get first dept ID
$deptResult = Api-Call "GET" "/department" -params @{ count = "1"; fields = "id" }
$firstDeptId = $deptResult.data.values[0].id
Write-Host "First dept ID: $firstDeptId"

Write-Host "`n================================================"
Write-Host "FOLLOW-UP EXPERIMENTS"
Write-Host "================================================"

# ============================================================
# TEST A: Inline employments WITH email (the big potential!)
# ============================================================
Write-Host "`n--- TEST A: Inline employments WITH email ---"
$tA = Api-Call "POST" "/employee" @{
    firstName = "InlineEmpWithEmail"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; email = "inline.emp@test.no"
    department = @{ id = $firstDeptId }
    employments = @(@{ startDate = "2025-01-01" })
}
Write-Host "  Status: $($tA.code) Success: $($tA.success)"
if ($tA.success) {
    $empIdA = $tA.data.value.id
    Write-Host "  Employee ID: $empIdA"
    Write-Host "  Employments in response: $(($tA.data.value.employments | ConvertTo-Json -Depth 3 -Compress) -replace '\s+',' ')"
    
    # Double check employments via API
    $emplsA = Api-Call "GET" "/employee/employment" -params @{ employeeId = "$empIdA"; fields = "id,startDate,division" }
    Write-Host "  Employments via separate GET: count=$($emplsA.data.fullResultSize)"
    if ($emplsA.data.values) {
        foreach ($e in $emplsA.data.values) {
            Write-Host "    Employment ID=$($e.id) startDate=$($e.startDate) division=$($e.division | ConvertTo-Json -Compress)"
        }
    }
} else {
    Write-Host "  Response: $($tA.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST B: Inline employments WITH email AND division
# ============================================================
Write-Host "`n--- TEST B: Inline employments WITH email AND division ---"
$divResult = Api-Call "GET" "/division" -params @{ count = "1"; fields = "id" }
$divId = $divResult.data.values[0].id
Write-Host "  Division ID: $divId"

$tB = Api-Call "POST" "/employee" @{
    firstName = "InlineEmpDiv"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; email = "inline.empdiv@test.no"
    department = @{ id = $firstDeptId }
    employments = @(@{ startDate = "2025-01-01"; division = @{ id = $divId } })
}
Write-Host "  Status: $($tB.code) Success: $($tB.success)"
if ($tB.success) {
    $empIdB = $tB.data.value.id
    Write-Host "  Employee ID: $empIdB"
    
    $emplsB = Api-Call "GET" "/employee/employment" -params @{ employeeId = "$empIdB"; fields = "id,startDate,division" }
    Write-Host "  Employments via GET: count=$($emplsB.data.fullResultSize)"
    if ($emplsB.data.values) {
        foreach ($e in $emplsB.data.values) {
            Write-Host "    Employment ID=$($e.id) startDate=$($e.startDate)"
        }
    }
} else {
    Write-Host "  Response: $($tB.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST C: Can we create employee without email if userType=STANDARD?
# (What if we skip userType entirely?)
# ============================================================
Write-Host "`n--- TEST C: POST employee without userType (omitted) ---"
$tC = Api-Call "POST" "/employee" @{
    firstName = "NoUserType"; lastName = "Test"; dateOfBirth = "1990-01-01"
    department = @{ id = $firstDeptId }
}
Write-Host "  Status: $($tC.code) Success: $($tC.success)"
if ($tC.success) {
    Write-Host "  Employee ID: $($tC.data.value.id) userType=$($tC.data.value.userType)"
} else {
    Write-Host "  Response: $($tC.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST D: userType NOT_TRIPLETEX_USER — does it skip email requirement?
# ============================================================
Write-Host "`n--- TEST D: userType=NOT_TRIPLETEX_USER (skip email?) ---"
$tD = Api-Call "POST" "/employee" @{
    firstName = "NotTxUser"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "NOT_TRIPLETEX_USER"
    department = @{ id = $firstDeptId }
}
Write-Host "  Status: $($tD.code) Success: $($tD.success)"
if ($tD.success) {
    Write-Host "  Employee ID: $($tD.data.value.id) userType=$($tD.data.value.userType)"
} else {
    Write-Host "  Response: $($tD.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST E: Create employee + inline employments + inline department creation
# Can we create a department inline with the employee?
# ============================================================
Write-Host "`n--- TEST E: Inline department creation in employee POST ---"
$tE = Api-Call "POST" "/employee" @{
    firstName = "InlineDept"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; email = "inline.dept@test.no"
    department = @{ name = "AutoDept"; departmentNumber = 42 }
}
Write-Host "  Status: $($tE.code) Success: $($tE.success)"
if ($tE.success) {
    Write-Host "  Employee ID: $($tE.data.value.id)"
    Write-Host "  Department: $($tE.data.value.department | ConvertTo-Json -Depth 2 -Compress)"
} else {
    Write-Host "  Response: $($tE.data | ConvertTo-Json -Depth 3 -Compress)"
}

Write-Host "`n================================================"
Write-Host "ALL FOLLOW-UP EXPERIMENTS COMPLETE"
Write-Host "================================================"
