<#
.SYNOPSIS
    Sandbox experiments for reducing employee API calls.
    Tests various strategies directly against the Tripletex sandbox API.
#>

# Load credentials from user-secrets
$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $Token = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $Token = $Matches[1].Trim() }
}
if (-not $BaseUrl -or -not $Token) { Write-Error "Could not load credentials"; return }

$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$Token"))
$headers = @{ "Authorization" = "Basic $cred"; "Content-Type" = "application/json" }

function Api-Get($path, $params = @{}) {
    $qs = ($params.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "&"
    $url = if ($qs) { "$BaseUrl$path`?$qs" } else { "$BaseUrl$path" }
    return Invoke-RestMethod -Uri $url -Headers $headers -Method Get
}

function Api-Post($path, $body) {
    $json = $body | ConvertTo-Json -Depth 5
    try {
        $r = Invoke-RestMethod -Uri "$BaseUrl$path" -Headers $headers -Method Post -Body $json
        return @{ success = $true; data = $r }
    }
    catch {
        $statusCode = $null
        $errorBody = $null
        try {
            $statusCode = $_.Exception.Response.StatusCode.value__
            $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            $errorBody = $reader.ReadToEnd()
        }
        catch {}
        return @{ success = $false; statusCode = $statusCode; error = $errorBody; message = $_.Exception.Message }
    }
}

function Api-Delete($path) {
    try {
        Invoke-RestMethod -Uri "$BaseUrl$path" -Headers $headers -Method Delete
        return @{ success = $true }
    }
    catch {
        return @{ success = $false; message = $_.Exception.Message }
    }
}

Write-Host "`n=========================================="
Write-Host "EMPLOYEE EFFICIENCY SANDBOX EXPERIMENTS"
Write-Host "==========================================`n"

# ============================================================
# EXPERIMENT 1: Query existing departments
# ============================================================
Write-Host "=== EXPERIMENT 1: Query existing departments ==="
$deptResult = Api-Get "/department" @{ count = "20"; fields = "id,name,departmentNumber" }
Write-Host "Total departments: $($deptResult.fullResultSize)"
foreach ($d in $deptResult.values) {
    Write-Host "  ID=$($d.id) name='$($d.name)' number=$($d.departmentNumber)"
}
$firstDeptId = $deptResult.values[0].id
Write-Host "First department ID: $firstDeptId"

# ============================================================
# EXPERIMENT 2: POST employee with hardcoded department ID=1
# ============================================================
Write-Host "`n=== EXPERIMENT 2: POST employee with hardcoded department ID=1 ==="
$r2 = Api-Post "/employee" @{
    firstName   = "TestHardcode"
    lastName    = "DeptIdOne"
    dateOfBirth = "1990-01-01"
    userType    = "STANDARD"
    department  = @{ id = 1 }
}
if ($r2.success) {
    Write-Host "SUCCESS: Created employee ID=$($r2.data.value.id) with hardcoded dept ID=1"
    # Clean up
    Api-Delete "/employee/$($r2.data.value.id)" | Out-Null
}
else {
    Write-Host "FAILED: Status=$($r2.statusCode)"
    Write-Host "Error: $($r2.error)"
}

# ============================================================
# EXPERIMENT 3: POST employee with department by departmentNumber
# ============================================================
Write-Host "`n=== EXPERIMENT 3a: POST employee with dept={departmentNumber:'1'} ==="
$r3a = Api-Post "/employee" @{
    firstName   = "TestInline"
    lastName    = "DeptByNum"
    dateOfBirth = "1990-01-01"
    userType    = "STANDARD"
    department  = @{ departmentNumber = "1" }
}
if ($r3a.success) {
    Write-Host "SUCCESS: Created employee ID=$($r3a.data.value.id) with dept by number"
    Api-Delete "/employee/$($r3a.data.value.id)" | Out-Null
}
else {
    Write-Host "FAILED: Status=$($r3a.statusCode)"
    Write-Host "Error: $($r3a.error)"
}

Write-Host "`n=== EXPERIMENT 3b: POST employee with dept={name:'Avdeling'} ==="
$r3b = Api-Post "/employee" @{
    firstName   = "TestInline"
    lastName    = "DeptByName"
    dateOfBirth = "1990-01-01"
    userType    = "STANDARD"
    department  = @{ name = "Avdeling" }
}
if ($r3b.success) {
    Write-Host "SUCCESS: Created employee ID=$($r3b.data.value.id) with dept by name"
    Api-Delete "/employee/$($r3b.data.value.id)" | Out-Null
}
else {
    Write-Host "FAILED: Status=$($r3b.statusCode)"
    Write-Host "Error: $($r3b.error)"
}

# ============================================================
# EXPERIMENT 4: POST employee WITHOUT department (no department ref)
# ============================================================
Write-Host "`n=== EXPERIMENT 4: POST employee WITHOUT department ==="
$r4 = Api-Post "/employee" @{
    firstName   = "TestNoDept"
    lastName    = "NoDepartment"
    dateOfBirth = "1990-01-01"
    userType    = "STANDARD"
}
if ($r4.success) {
    Write-Host "SUCCESS: Created employee ID=$($r4.data.value.id) WITHOUT department!"
    $exp4EmpId = $r4.data.value.id
}
else {
    Write-Host "FAILED: Status=$($r4.statusCode)"
    Write-Host "Error: $($r4.error)"
}

# ============================================================
# EXPERIMENT 5: POST employee WITH department (known good dept), NO employment
# ============================================================
Write-Host "`n=== EXPERIMENT 5: POST employee with dept, but NO employment record ==="
$r5 = Api-Post "/employee" @{
    firstName   = "TestNoEmp"
    lastName    = "NoEmployment"
    dateOfBirth = "1990-01-01"
    userType    = "STANDARD"
    email       = "test.noemp@example.com"
    department  = @{ id = $firstDeptId }
}
if ($r5.success) {
    $empId5 = $r5.data.value.id
    Write-Host "SUCCESS: Created employee ID=$empId5 with dept=$firstDeptId but NO employment"
    
    # Verify the employee exists and has all expected fields
    $verify = Api-Get "/employee/$empId5" @{ fields = "id,firstName,lastName,email,department,employments" }
    Write-Host "  Verified - firstName=$($verify.value.firstName) lastName=$($verify.value.lastName) email=$($verify.value.email)"
    Write-Host "  Department: id=$($verify.value.department.id)"
    $empCount = 0
    if ($verify.value.employments) { $empCount = $verify.value.employments.Count }
    Write-Host "  Employments count: $empCount (expected: 0 since we skipped employment POST)"
}
else {
    Write-Host "FAILED: Status=$($r5.statusCode)"
    Write-Host "Error: $($r5.error)"
}

# ============================================================
# EXPERIMENT 6: Query divisions
# ============================================================
Write-Host "`n=== EXPERIMENT 6: Query divisions ==="
$divResult = Api-Get "/division" @{ count = "20"; fields = "id,name" }
Write-Host "Total divisions: $($divResult.fullResultSize)"
foreach ($d in $divResult.values) {
    Write-Host "  ID=$($d.id) name='$($d.name)'"
}

# ============================================================
# EXPERIMENT 7: POST employee with dept + employment in ONE call?
# Test if we can inline employment data in the employee POST
# ============================================================
Write-Host "`n=== EXPERIMENT 7: POST employee with employments array inline ==="
$r7 = Api-Post "/employee" @{
    firstName   = "TestInlineEmp"
    lastName    = "WithEmployment"
    dateOfBirth = "1990-01-01"
    userType    = "STANDARD"
    department  = @{ id = $firstDeptId }
    employments = @(
        @{
            startDate = "2025-01-01"
        }
    )
}
if ($r7.success) {
    $empId7 = $r7.data.value.id
    Write-Host "SUCCESS: Created employee ID=$empId7 WITH inline employments"
    $verify7 = Api-Get "/employee/$empId7" @{ fields = "id,firstName,lastName,employments" }
    $empCount7 = 0
    if ($verify7.value.employments) { $empCount7 = $verify7.value.employments.Count }
    Write-Host "  Employments count: $empCount7"
}
else {
    Write-Host "FAILED: Status=$($r7.statusCode)"
    Write-Host "Error: $($r7.error)"
}

# ============================================================
# EXPERIMENT 8: POST employee + employment separately, but skip GET /division
# (test if employment works without division in sandbox)
# ============================================================
Write-Host "`n=== EXPERIMENT 8: POST employment WITHOUT division ==="
$r8emp = Api-Post "/employee" @{
    firstName   = "TestNoDivEmp"
    lastName    = "SkipDivision"
    dateOfBirth = "1990-01-01"
    userType    = "STANDARD"
    department  = @{ id = $firstDeptId }
}
if ($r8emp.success) {
    $empId8 = $r8emp.data.value.id
    Write-Host "Created employee ID=$empId8"
    
    $r8employment = Api-Post "/employee/employment" @{
        employee  = @{ id = $empId8 }
        startDate = "2025-01-01"
    }
    if ($r8employment.success) {
        Write-Host "SUCCESS: Created employment WITHOUT division"
    }
    else {
        Write-Host "FAILED to create employment without division: Status=$($r8employment.statusCode)"
        Write-Host "Error: $($r8employment.error)"
    }
}
else {
    Write-Host "FAILED to create employee: Status=$($r8emp.statusCode)"
}

Write-Host "`n=========================================="
Write-Host "ALL EXPERIMENTS COMPLETE"
Write-Host "==========================================`n"
