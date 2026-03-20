<#
.SYNOPSIS
    Starts a cloudflared tunnel to expose the agent via HTTPS.
.DESCRIPTION
    Uses Cloudflare's quick tunnel (no account needed) to expose localhost:5000.
    No interstitial page, no rate limits — more reliable than ngrok free tier.
.PARAMETER Port
    Local port to tunnel (default: 5000).
.PARAMETER Kill
    Stop any running cloudflared process and exit.
.PARAMETER FixDns
    Fix DNS resolution issues for *.trycloudflare.com (requires Admin).
    Some ISPs (e.g. Telenor) fail to resolve these domains. This adds a
    hosts entry for api.trycloudflare.com and switches DNS to 1.1.1.1/8.8.8.8.
#>
param(
    [int]$Port = 5000,
    [switch]$Kill,
    [switch]$FixDns
)

# --- Kill mode ---
if ($Kill) {
    $procs = Get-Process -Name cloudflared -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        Write-Host "cloudflared stopped." -ForegroundColor Yellow
    }
    else {
        Write-Host "cloudflared is not running." -ForegroundColor Gray
    }
    return
}

# --- DNS fix mode ---
# Some ISPs (e.g. Telenor in Norway) have DNS resolvers that can't resolve
# *.trycloudflare.com. This adds a static hosts entry and switches to public DNS.
if ($FixDns) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "ERROR: -FixDns requires an elevated (Admin) PowerShell session." -ForegroundColor Red
        return
    }

    $hostsFile = "C:\Windows\System32\drivers\etc\hosts"
    $hostsEntry = "104.16.230.132 api.trycloudflare.com"
    $hostsContent = Get-Content $hostsFile -Raw -ErrorAction SilentlyContinue
    if ($hostsContent -notmatch [regex]::Escape("api.trycloudflare.com")) {
        Add-Content -Path $hostsFile -Value "`n$hostsEntry"
        Write-Host "Added hosts entry: $hostsEntry" -ForegroundColor Green
    }
    else {
        Write-Host "Hosts entry for api.trycloudflare.com already exists." -ForegroundColor Gray
    }

    # Detect active network adapter
    $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1
    if ($adapter) {
        $alias = $adapter.Name
        Set-DnsClientServerAddress -InterfaceAlias $alias -ServerAddresses ("1.1.1.1","8.8.8.8","2606:4700:4700::1111","2606:4700:4700::1001")
        Write-Host "DNS servers set to 1.1.1.1 / 8.8.8.8 on adapter '$alias'" -ForegroundColor Green
    }
    else {
        Write-Host "WARNING: No active network adapter found. Set DNS manually." -ForegroundColor Yellow
    }

    Write-Host "DNS fix applied. Restart cloudflared now." -ForegroundColor Cyan
    return
}

# Ensure agent is running
$agent = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if (-not $agent) {
    Write-Host "WARNING: TripletexAgent is not running. Start it first with .\scripts\Start-Agent.ps1" -ForegroundColor Yellow
}

# Kill existing cloudflared if already running
$existing = Get-Process -Name cloudflared -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing cloudflared process..." -ForegroundColor Yellow
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# Start cloudflared and capture output to find the URL
Write-Host "Starting cloudflared tunnel to http://localhost:$Port ..." -ForegroundColor Cyan

$cloudflaredExe = Get-Command cloudflared -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $cloudflaredExe) {
    # Try winget package location
    $cloudflaredExe = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\*cloudflare*" -Recurse -Filter "cloudflared.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $cloudflaredExe) {
    Write-Host "ERROR: cloudflared not found. Install with: winget install Cloudflare.cloudflared" -ForegroundColor Red
    return
}

$logFile = Join-Path $PSScriptRoot "..\src\logs\cloudflared.log"
Start-Process -FilePath $cloudflaredExe `
    -ArgumentList "tunnel", "--url", "http://localhost:$Port", "--no-autoupdate" `
    -RedirectStandardError $logFile `
    -WindowStyle Hidden

# Wait for the URL to appear in the log
$maxWait = 20
$waited = 0
$tunnelUrl = $null
while ($waited -lt $maxWait) {
    Start-Sleep -Seconds 1
    $waited++
    if (Test-Path $logFile) {
        $content = Get-Content $logFile -Raw -ErrorAction SilentlyContinue
        if ($content -match '(https://[a-z0-9-]+\.trycloudflare\.com)') {
            $tunnelUrl = $Matches[1]
            break
        }
    }
}

if (-not $tunnelUrl) {
    Write-Host "ERROR: Could not get tunnel URL after ${maxWait}s." -ForegroundColor Red
    Write-Host "Check log: $logFile" -ForegroundColor Gray
    return
}

# Display submission info
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " cloudflared tunnel is running!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Endpoint URL:  " -NoNewline; Write-Host "$tunnelUrl/solve" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Submit at:     https://app.ainm.no/submit/tripletex" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Stop with:     .\scripts\Start-Cloudflared.ps1 -Kill" -ForegroundColor Gray
