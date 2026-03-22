Set-Location "C:\Users\JanØdegård\Documents\Github\hackathon.nm-i-ai.tripletex\src"; dotnet build --no-restore *> build_log.txt; Get-Content build_log.txt | Select-Object -Last 25
