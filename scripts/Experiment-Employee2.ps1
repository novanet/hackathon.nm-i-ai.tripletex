<#
.SYNOPSIS
    Follow-up experiments with better error reporting
#>

$secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
$BaseUrl = $null; $Token = $null
foreach ($line in $secretsJson) {
    if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$') { $BaseUrl = $Matches[1].Trim() }
    if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$') { $Token = $Matches[1].Trim() }
}

$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("0:$Token"))
$headers = @{ "Authorization" = "Basic $cred"; "Content-Type" = "application/json" }

function Api-Get($path, $params = @{}) {
    $qs = ($params.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "&"
    $url = if ($qs) { "$BaseUrl$path`?$qs" } else { "$BaseUrl$path" }
    return Invoke-RestMethod -Uri $url -Headers $headers -Method Get
}

function Api-Post($path, $bodyObj) {
    $json = $bodyObj | ConvertTo-Json -Depth 5
    Write-Host "  >>> POST $path"
    Write-Host "  Body: $json"
    try {
        $r = Invoke-RestMethod -Uri "$BaseUrl$path" -Headers $headers -Method Post -Body $json
        return @{ success = $true; data = $r }
    }
    catch {
        $statusCode = $null
        $errorBody = "N/A"
        try {
            $statusCode = $_.Exception.Response.StatusCode.value__
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream) {
                $reader = [System.IO.StreamReader]::new($stream)
                $errorBody = $reader.ReadToEnd()
            }
        }
        catch {}
        Write-Host "  <<< FAILED: HTTP $statusCode"
        Write-Host "  Error body: $errorBody"
        return @{ success = $false; statusCode = $statusCode; error = $errorBody }
    }
}

# Get first dept ID
$deptResult = Api-Get "/department" @{ count = "1"; fields = "id" }
$firstDeptId = $deptResult.values[0].id
Write-Host "First dept ID: $firstDeptId"

# ============================================================
# RE-RUN: Better error details for key failures
# ============================================================

Write-Host "`n=== TEST A: POST employee with dept id=1 (hardcoded) ==="
Api-Post "/employee" @{
    firstName = "TestA"; lastName = "HardcodedDept"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = 1 }
} | Out-Null

Write-Host "`n=== TEST B: POST employee WITHOUT department ==="
Api-Post "/employee" @{
    firstName = "TestB"; lastName = "NoDept"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"
} | Out-Null

Write-Host "`n=== TEST C: POST employee with dept={departmentNumber:1} ==="
Api-Post "/employee" @{
    firstName = "TestC"; lastName = "DeptByNum"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ departmentNumber = 1 }
} | Out-Null

Write-Host "`n=== TEST D: POST employment without division (created emp first) ==="
$rd = Api-Post "/employee" @{
    firstName = "TestD"; lastName = "NoDivEmp"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = $firstDeptId }
}
if ($rd.success) {
    $empIdD = $rd.data.value.id
    Write-Host "  Employee created: ID=$empIdD"
    Api-Post "/employee/employment" @{
        employee  = @{ id = $empIdD }
        startDate = "2025-01-01"
    } | Out-Null
}

Write-Host "`n=== TEST E: POST employee with inline employments ==="
Api-Post "/employee" @{
    firstName = "TestE"; lastName = "InlineEmp"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = $firstDeptId }
    employments = @(@{ startDate = "2025-01-01" })
} | Out-Null

Write-Host "`n=== TEST F: Can we POST with dept={id:0} (auto-resolve)? ==="
Api-Post "/employee" @{
    firstName = "TestF"; lastName = "DeptZero"; dateOfBirth = "1990-01-01"
    userType = "STANDARD"; department = @{ id = 0 }
} | Out-Null

Write-Host "`n=== ALL FOLLOW-UP TESTS COMPLETE ==="
