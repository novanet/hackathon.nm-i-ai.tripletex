<#
.SYNOPSIS
    Final employee experiments with robust error handling via -SkipHttpErrorCheck
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

    $splat = @{
        Uri                  = $url
        Headers              = $headers
        Method               = $method
        SkipHttpErrorCheck   = $true
        StatusCodeVariable   = 'sc'
        ResponseHeadersVariable = 'rh'
    }
    if ($bodyObj) {
        $splat['Body'] = ($bodyObj | ConvertTo-Json -Depth 5)
    }
    
    $response = Invoke-RestMethod @splat
    $code = $sc
    
    if ($code -ge 200 -and $code -lt 300) {
        return @{ success = $true; code = $code; data = $response }
    } else {
        return @{ success = $false; code = $code; data = $response }
    }
}

# Verify auth first
Write-Host "=== VERIFY AUTH ==="
$whoami = Api-Call "GET" "/token/session/>whoAmI"
if ($whoami.success) {
    Write-Host "Auth OK: $($whoami.data.value.employee.firstName) $($whoami.data.value.employee.lastName) (company: $($whoami.data.value.company.name))"
} else {
    Write-Host "Auth FAILED: $($whoami.code)"
    Write-Host ($whoami.data | ConvertTo-Json -Depth 3)
    return
}

# Get first dept ID
$deptResult = Api-Call "GET" "/department" -params @{ count = "1"; fields = "id,name" }
$firstDeptId = $deptResult.data.values[0].id
$firstDeptName = $deptResult.data.values[0].name
Write-Host "First dept: ID=$firstDeptId name='$firstDeptName'"

Write-Host "`n================================================"
Write-Host "EXPERIMENTS"
Write-Host "================================================"

# ============================================================
# TEST 1: Baseline - create employee with correct dept (should succeed)
# ============================================================
Write-Host "`n--- TEST 1: Baseline (correct dept ID) ---"
$t1 = Api-Call "POST" "/employee" @{
    firstName = "Baseline"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = $firstDeptId }; email = "baseline@test.no"
}
Write-Host "  Status: $($t1.code) Success: $($t1.success)"
if ($t1.success) {
    $baseId = $t1.data.value.id
    Write-Host "  Employee ID: $baseId"
} else {
    Write-Host "  Response: $($t1.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST 2: POST employee with hardcoded dept ID=1
# ============================================================
Write-Host "`n--- TEST 2: Hardcoded dept ID=1 ---"
$t2 = Api-Call "POST" "/employee" @{
    firstName = "HCDept"; lastName = "IdOne"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = 1 }
}
Write-Host "  Status: $($t2.code) Success: $($t2.success)"
if (-not $t2.success) {
    Write-Host "  Response: $($t2.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST 3: POST employee WITHOUT department
# ============================================================
Write-Host "`n--- TEST 3: No department ---"
$t3 = Api-Call "POST" "/employee" @{
    firstName = "NoDept"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"
}
Write-Host "  Status: $($t3.code) Success: $($t3.success)"
if (-not $t3.success) {
    Write-Host "  Response: $($t3.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST 4: POST employee WITHOUT employment (key experiment!)
# ============================================================
Write-Host "`n--- TEST 4: With dept, WITHOUT employment (KEY TEST) ---"
$t4 = Api-Call "POST" "/employee" @{
    firstName = "NoEmployment"; lastName = "KeyTest"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = $firstDeptId }; email = "no.employment@test.no"
}
Write-Host "  Status: $($t4.code) Success: $($t4.success)"
if ($t4.success) {
    $noEmpId = $t4.data.value.id
    Write-Host "  Employee ID: $noEmpId"
    
    # Verify the employee
    $verify = Api-Call "GET" "/employee/$noEmpId" -params @{ fields = "*" }
    Write-Host "  Verified: firstName=$($verify.data.value.firstName) lastName=$($verify.data.value.lastName)"
    Write-Host "  email=$($verify.data.value.email)"
    Write-Host "  department.id=$($verify.data.value.department.id)"
    Write-Host "  dateOfBirth=$($verify.data.value.dateOfBirth)"
    
    # Check employments
    $empls = Api-Call "GET" "/employee/employment" -params @{ employeeId = "$noEmpId"; fields = "id,startDate" }
    Write-Host "  Employments found: $($empls.data.fullResultSize)"
} else {
    Write-Host "  Response: $($t4.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST 5: POST employee + employment WITHOUT division
# ============================================================
Write-Host "`n--- TEST 5: Employment without division ---"
$t5 = Api-Call "POST" "/employee" @{
    firstName = "NoDivision"; lastName = "Employment"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = $firstDeptId }
}
Write-Host "  Status: $($t5.code) Success: $($t5.success)"
if ($t5.success) {
    $noDivEmpId = $t5.data.value.id
    Write-Host "  Employee ID: $noDivEmpId"
    
    $t5emp = Api-Call "POST" "/employee/employment" @{
        employee  = @{ id = $noDivEmpId }
        startDate = "2025-01-01"
    }
    Write-Host "  Employment POST status: $($t5emp.code) Success: $($t5emp.success)"
    if (-not $t5emp.success) {
        Write-Host "  Response: $($t5emp.data | ConvertTo-Json -Depth 3 -Compress)"
    }
}

# ============================================================
# TEST 6: POST employee with dept by departmentNumber
# ============================================================
Write-Host "`n--- TEST 6: dept={departmentNumber:1} ---"
$t6 = Api-Call "POST" "/employee" @{
    firstName = "DeptNum"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ departmentNumber = 1 }
}
Write-Host "  Status: $($t6.code) Success: $($t6.success)"
if (-not $t6.success) {
    Write-Host "  Response: $($t6.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST 7: POST employee with inline employments
# ============================================================
Write-Host "`n--- TEST 7: Inline employments array ---"
$t7 = Api-Call "POST" "/employee" @{
    firstName = "InlineEmp"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = $firstDeptId }
    employments = @(@{ startDate = "2025-01-01" })
}
Write-Host "  Status: $($t7.code) Success: $($t7.success)"
if ($t7.success) {
    Write-Host "  Employee ID: $($t7.data.value.id)"
    Write-Host "  Employments in response: $($t7.data.value.employments | ConvertTo-Json -Depth 3 -Compress)"
} else {
    Write-Host "  Response: $($t7.data | ConvertTo-Json -Depth 3 -Compress)"
}

# ============================================================
# TEST 8: Can dept ID=0 be used? (some APIs treat 0 as "use default")
# ============================================================
Write-Host "`n--- TEST 8: dept={id:0} ---"
$t8 = Api-Call "POST" "/employee" @{
    firstName = "DeptZero"; lastName = "Test"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = 0 }
}
Write-Host "  Status: $($t8.code) Success: $($t8.success)"
if ($t8.success) {
    Write-Host "  Employee created with dept id=0!"
    Write-Host "  Actual dept in response: $($t8.data.value.department | ConvertTo-Json -Depth 2 -Compress)"
} else {
    Write-Host "  Response: $($t8.data | ConvertTo-Json -Depth 3 -Compress)"
}

Write-Host "`n================================================"
Write-Host "ALL EXPERIMENTS COMPLETE"
Write-Host "================================================"
