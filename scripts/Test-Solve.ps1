<#
.SYNOPSIS
    Sends a test prompt to the locally running /solve endpoint.

.DESCRIPTION
    Sends a POST request to http://localhost:5000/solve with the given prompt
    and Tripletex sandbox credentials. Useful for verifying handler behavior
    during development.

.PARAMETER Prompt
    The accounting task prompt to send (any language).

.PARAMETER BaseUrl
    Tripletex API base URL. Defaults to the sandbox URL from user-secrets.

.PARAMETER SessionToken
    Tripletex session token. Defaults to the sandbox token from user-secrets.

.PARAMETER ApiKey
    Bearer token for the /solve endpoint. Defaults to the configured API key.

.PARAMETER Port
    Local port the agent is listening on. Defaults to 5000.

.EXAMPLE
    .\scripts\Test-Solve.ps1 "Opprett en kunde med navn 'Test AS'"

.EXAMPLE
    .\scripts\Test-Solve.ps1 -Prompt "Create an employee named Ola Nordmann" -Port 5001
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Prompt,

    [string[]]$FilePaths,

    [string]$BaseUrl,
    [string]$SessionToken,
    [string]$ApiKey,
    [int]$Port = 5000
)

# Load defaults from user-secrets if not provided
if (-not $BaseUrl -or -not $SessionToken -or -not $ApiKey) {
    $secretsJson = dotnet user-secrets list --project "$PSScriptRoot\..\src" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>$null
    if ($secretsJson) {
        foreach ($line in $secretsJson) {
            if ($line -match '^Tripletex:BaseUrl\s*=\s*(.+)$' -and -not $BaseUrl) {
                $BaseUrl = $Matches[1].Trim()
            }
            if ($line -match '^Tripletex:SessionToken\s*=\s*(.+)$' -and -not $SessionToken) {
                $SessionToken = $Matches[1].Trim()
            }
            if ($line -match '^ApiKey\s*=\s*(.+)$' -and -not $ApiKey) {
                $ApiKey = $Matches[1].Trim()
            }
        }
    }
}

# Require all values
if (-not $BaseUrl) { Write-Error "BaseUrl not set. Pass -BaseUrl or configure Tripletex:BaseUrl in user-secrets."; return }
if (-not $SessionToken) { Write-Error "SessionToken not set. Pass -SessionToken or configure Tripletex:SessionToken in user-secrets."; return }
if (-not $ApiKey) { Write-Error "ApiKey not set. Pass -ApiKey or configure ApiKey in user-secrets."; return }

$body = @{
    prompt                = $Prompt
    tripletex_credentials = @{
        base_url      = $BaseUrl
        session_token = $SessionToken
    }
}

if ($FilePaths) {
    $body.files = @(foreach ($fp in $FilePaths) {
            $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $fp))
            $ext = [System.IO.Path]::GetExtension($fp).ToLower()
            $mime = switch ($ext) {
                '.pdf' { 'application/pdf' }
                '.png' { 'image/png' }
                '.jpg' { 'image/jpeg' }
                '.jpeg' { 'image/jpeg' }
                '.gif' { 'image/gif' }
                '.webp' { 'image/webp' }
                default { 'application/octet-stream' }
            }
            @{
                filename       = [System.IO.Path]::GetFileName($fp)
                content_base64 = [Convert]::ToBase64String($bytes)
                mime_type      = $mime
            }
        })
}

$body = $body | ConvertTo-Json -Depth 5

$url = "http://localhost:$Port/solve"

$wc = [System.Net.WebClient]::new()
$wc.Headers.Add("Content-Type", "application/json")
$wc.Headers.Add("Authorization", "Bearer $ApiKey")

try {
    $response = $wc.UploadString($url, "POST", $body)
    Write-Host "OK: $response" -ForegroundColor Green
}
catch {
    Write-Host "ERR: $($_.Exception.InnerException.Message)" -ForegroundColor Red
}
finally {
    $wc.Dispose()
}

# Show latest log tail
$logDir = Join-Path $PSScriptRoot "..\src\logs"
if (Test-Path $logDir) {
    $latest = Get-ChildItem "$logDir\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        Write-Host "`n--- Log tail ---" -ForegroundColor Cyan
        Get-Content $latest.FullName -Tail 10
    }
}
