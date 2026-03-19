<#
.SYNOPSIS
    Stops any running TripletexAgent and starts a fresh instance.
.DESCRIPTION
    Kills existing TripletexAgent processes, waits for the file lock to
    release, then runs the project. Use -Background to start in the
    background and return immediately.
#>
param(
    [switch]$Background
)

$project = Join-Path $PSScriptRoot "..\src\TripletexAgent.csproj"

# Kill existing instances
$procs = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
if ($procs) {
    $procs | ForEach-Object {
        Write-Host "Killing TripletexAgent PID $($_.Id)..." -ForegroundColor Yellow
        $_.Kill()
    }
    Start-Sleep -Seconds 2
}

# Build first so startup is fast and errors surface early
Write-Host "Building..." -ForegroundColor Cyan
dotnet build $project --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    return
}

Write-Host "Starting TripletexAgent..." -ForegroundColor Cyan

if ($Background) {
    # Use Start-Process with -WindowStyle Hidden for a truly detached process
    $resolved = (Resolve-Path $project).Path
    Start-Process -FilePath "dotnet" -ArgumentList "run", "--project", $resolved, "--no-build" -WindowStyle Hidden

    # Poll for the process to appear (up to 10 seconds)
    $found = $false
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 1
        $newProc = Get-Process -Name TripletexAgent -ErrorAction SilentlyContinue
        if ($newProc) {
            Write-Host "Running (PID $($newProc.Id))" -ForegroundColor Green
            $found = $true
            break
        }
    }
    if (-not $found) {
        Write-Host "Process did not appear within 10s — check logs." -ForegroundColor Red
    }
}
else {
    dotnet run --project $project --no-build
}
