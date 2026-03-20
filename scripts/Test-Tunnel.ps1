<#
.SYNOPSIS
    Test the tunnel endpoint with competition-like headers (no Bypass-Tunnel-Reminder).
.PARAMETER Url
    The tunnel URL to test. Defaults to reading from localtunnel.log.
#>
param([string]$Url)

if (-not $Url) {
    # Try localtunnel
    $ltLog = Join-Path $PSScriptRoot "..\src\logs\localtunnel.log"
    if (Test-Path $ltLog) {
        $content = Get-Content $ltLog -Raw
        if ($content -match '(https://[a-z0-9-]+\.loca\.lt)') { $Url = $Matches[1] }
    }
    # Try ngrok
    if (-not $Url) {
        try {
            $resp = Invoke-RestMethod -Uri "http://localhost:4040/api/tunnels" -ErrorAction Stop
            $t = $resp.tunnels | Where-Object { $_.proto -eq "https" } | Select-Object -First 1
            if ($t) { $Url = $t.public_url }
        } catch {}
    }
}

if (-not $Url) {
    Write-Host "No tunnel URL found." -ForegroundColor Red
    return
}

Write-Host "Testing: $Url/solve" -ForegroundColor Cyan
Write-Host "Headers: mimicking python-httpx/0.28.1 (no Bypass-Tunnel-Reminder)" -ForegroundColor Gray

$body = '{"prompt":"ping","files":[],"tripletex_credentials":{"base_url":"https://test","session_token":"test"}}'

$headers = @{
    "Content-Type"    = "application/json"
    "User-Agent"      = "python-httpx/0.28.1"
    "Accept"          = "*/*"
    "Accept-Encoding" = "gzip, deflate"
}

try {
    $response = Invoke-WebRequest -Uri "$Url/solve" -Method POST -Headers $headers -Body $body `
        -TimeoutSec 15 -ErrorAction Stop
    
    Write-Host ""
    Write-Host "Status: $($response.StatusCode)" -ForegroundColor Green
    Write-Host "Content-Type: $($response.Headers['Content-Type'])" -ForegroundColor Gray
    
    $bodyText = $response.Content
    if ($bodyText.Length -gt 500) {
        Write-Host "Response (first 500 chars):" -ForegroundColor Yellow
        Write-Host $bodyText.Substring(0, 500)
        Write-Host "..."
        if ($bodyText -match '<html|<!DOCTYPE') {
            Write-Host ""
            Write-Host "RESULT: Got HTML splash page - tunnel will NOT work for competition!" -ForegroundColor Red
        }
    }
    else {
        Write-Host "Response: $bodyText" -ForegroundColor Green
        if ($bodyText -match '"status"') {
            Write-Host "RESULT: Request reached the agent - tunnel works!" -ForegroundColor Green
        }
    }
}
catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    Write-Host "HTTP Error: $statusCode" -ForegroundColor Red
    Write-Host $_.Exception.Message
}
