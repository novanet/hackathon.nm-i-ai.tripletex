using System.Text.Json;
using System.Text.RegularExpressions;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class CostAnalysisHandler : ITaskHandler
{
    private readonly ILogger<CostAnalysisHandler> _logger;

    public CostAnalysisHandler(ILogger<CostAnalysisHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var handlerResult = new HandlerResult { EntityType = "project" };

        // Parse which two months to compare from the prompt (default: Jan vs Feb 2026)
        var (month1Start, month1End, month2Start, month2End) = ParseMonths(extracted);
        _logger.LogInformation("Cost analysis: comparing {M1Start}–{M1End} vs {M2Start}–{M2End}",
            month1Start, month1End, month2Start, month2End);

        // Step 1: GET ledger for month 1 (dateFrom inclusive, dateTo exclusive)
        var month1Data = await GetLedgerData(api, month1Start, month1End);

        // Step 2: GET ledger for month 2
        var month2Data = await GetLedgerData(api, month2Start, month2End);

        // Step 3: Build per-account sums, filter expense accounts (4000-7999)
        var month1Sums = ExtractExpenseAccountSums(month1Data);
        var month2Sums = ExtractExpenseAccountSums(month2Data);

        // Step 4: Calculate increases and find top 3
        var increases = new List<(int accountNumber, string accountName, long accountId, decimal increase)>();
        var allAccountNumbers = new HashSet<int>(month1Sums.Keys);
        foreach (var k in month2Sums.Keys) allAccountNumbers.Add(k);

        foreach (var accNum in allAccountNumbers)
        {
            var m1 = month1Sums.GetValueOrDefault(accNum);
            var m2 = month2Sums.GetValueOrDefault(accNum);
            var increase = m2.sum - m1.sum;
            if (increase > 0)
            {
                var name = !string.IsNullOrEmpty(m2.name) ? m2.name : m1.name;
                var id = m2.id > 0 ? m2.id : m1.id;
                increases.Add((accNum, name, id, increase));
            }
        }

        increases.Sort((a, b) => b.increase.CompareTo(a.increase));
        var top3 = increases.Take(3).ToList();

        _logger.LogInformation("Top 3 expense account increases: {Top3}",
            string.Join(", ", top3.Select(t => $"{t.accountNumber} ({t.accountName}): +{t.increase}")));

        if (top3.Count == 0)
        {
            _logger.LogError("COST ANALYSIS FAILED: No expense accounts with positive increase found. " +
                "Month1 had {M1Count} expense accounts, Month2 had {M2Count} expense accounts. " +
                "This likely means the /ledger endpoint did not return account fields.",
                month1Sums.Count, month2Sums.Count);
            return handlerResult;
        }

        // Step 5: Resolve project manager (required field — use first employee)
        long? managerId = null;
        var empResult = await api.GetAsync("/employee", new Dictionary<string, string>
        {
            ["count"] = "1",
            ["fields"] = "id"
        });
        if (empResult.TryGetProperty("values", out var empValues) && empValues.GetArrayLength() > 0)
            managerId = empValues[0].GetProperty("id").GetInt64();

        var today = DateTime.Today.ToString("yyyy-MM-dd");

        // Step 6: Create projects + activities for each top 3 account
        foreach (var (accountNumber, accountName, accountId, increase) in top3)
        {
            var projectName = accountName;
            if (string.IsNullOrEmpty(projectName))
                projectName = $"Konto {accountNumber}";

            var projectBody = new Dictionary<string, object>
            {
                ["name"] = projectName,
                ["isInternal"] = true,
                ["startDate"] = today
            };
            if (managerId.HasValue)
                projectBody["projectManager"] = new { id = managerId.Value };

            var projectResult = await api.PostAsync("/project", projectBody);
            var projectId = projectResult.GetProperty("value").GetProperty("id").GetInt64();
            _logger.LogInformation("Created project '{Name}' (ID: {Id}) for account {AccNum} (increase: {Increase})",
                projectName, projectId, accountNumber, increase);

            if (handlerResult.EntityId == null)
                handlerResult.EntityId = projectId;
            else
                handlerResult.AdditionalEntityIds.Add(projectId);

            // Create activity
            var activityBody = new Dictionary<string, object>
            {
                ["name"] = projectName,
                ["activityType"] = "PROJECT_GENERAL_ACTIVITY"
            };
            var activityResult = await api.PostAsync("/activity", activityBody);
            var activityId = activityResult.GetProperty("value").GetProperty("id").GetInt64();

            // Link activity to project
            await api.PostAsync("/project/projectActivity", new
            {
                project = new { id = projectId },
                activity = new { id = activityId }
            });
            _logger.LogInformation("Created and linked activity (ID: {ActivityId}) to project {ProjectId}", activityId, projectId);
        }

        return handlerResult;
    }

    private async Task<JsonElement> GetLedgerData(TripletexApiClient api, string dateFrom, string dateTo)
    {
        // Paginate to get all data
        var allValues = new List<JsonElement>();
        int from = 0;
        const int pageSize = 1000;

        while (true)
        {
            var result = await api.GetAsync("/ledger", new Dictionary<string, string>
            {
                ["dateFrom"] = dateFrom,
                ["dateTo"] = dateTo,
                ["from"] = from.ToString(),
                ["count"] = pageSize.ToString(),
                ["fields"] = "account(id,number,name),sumAmount"
            });

            if (result.TryGetProperty("values", out var values))
            {
                foreach (var v in values.EnumerateArray())
                    allValues.Add(v.Clone());

                if (values.GetArrayLength() < pageSize)
                    break;
                from += pageSize;
            }
            else
            {
                break;
            }
        }

        _logger.LogInformation("Got {Count} ledger accounts for period {From}–{To}", allValues.Count, dateFrom, dateTo);

        // Return as a synthetic element with values array
        var json = JsonSerializer.Serialize(new { values = allValues });
        return JsonDocument.Parse(json).RootElement;
    }

    private Dictionary<int, (decimal sum, string name, long id)> ExtractExpenseAccountSums(JsonElement data)
    {
        var sums = new Dictionary<int, (decimal sum, string name, long id)>();

        if (!data.TryGetProperty("values", out var values))
            return sums;

        foreach (var entry in values.EnumerateArray())
        {
            if (!entry.TryGetProperty("account", out var account))
                continue;

            int accountNumber = 0;
            if (account.TryGetProperty("number", out var numProp))
                accountNumber = numProp.GetInt32();

            // Filter expense accounts: 4000-7999
            if (accountNumber < 4000 || accountNumber > 7999)
                continue;

            string accountName = "";
            if (account.TryGetProperty("name", out var nameProp))
                accountName = nameProp.GetString() ?? "";

            long accountId = 0;
            if (account.TryGetProperty("id", out var idProp))
                accountId = idProp.GetInt64();

            decimal sumAmount = 0;
            if (entry.TryGetProperty("sumAmount", out var sumProp))
                sumAmount = sumProp.GetDecimal();

            sums[accountNumber] = (sumAmount, accountName, accountId);
        }

        return sums;
    }

    private (string month1Start, string month1End, string month2Start, string month2End) ParseMonths(ExtractionResult extracted)
    {
        // Default: compare January vs February of current year (2026)
        var year = 2026;

        // Try to extract year from prompt
        var rawPrompt = extracted.RawPrompt ?? "";
        var yearMatch = Regex.Match(rawPrompt, @"\b(20\d{2})\b");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var parsedYear))
            year = parsedYear;

        // Detect which months are mentioned
        var (m1, m2) = DetectMonths(rawPrompt);

        // Build month start/end date strings (dateTo is exclusive — first day of NEXT month)
        static string MonthStart(int y, int m) => $"{y}-{m:D2}-01";
        static string MonthEnd(int y, int m) => m == 12 ? $"{y + 1}-01-01" : $"{y}-{m + 1:D2}-01";

        return (MonthStart(year, m1), MonthEnd(year, m1), MonthStart(year, m2), MonthEnd(year, m2));
    }

    private (int month1, int month2) DetectMonths(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        // Multilingual month detection (prioritize first two month names found)
        var monthPatterns = new (string pattern, int month)[]
        {
            (@"\bjanuar[iy]?\b|\bjan\b|\benero\b|\bjaneiro\b|\bjanvier\b", 1),
            (@"\bfebruar[iy]?\b|\bfeb\b|\bfebrero\b|\bfevereiro\b|\bf[eé]vrier\b", 2),
            (@"\bmars\b|\bmar[csz]\b|\bmarch\b|\bmarzo\b|\bmar[çc]o\b", 3),
            (@"\bapril\b|\bapr\b|\babril\b|\bavril\b", 4),
            (@"\bmai\b|\bmay\b|\bmayo\b|\bmaio\b", 5),
            (@"\bjuni\b|\bjun[ie]?\b|\bjunio\b|\bjunho\b|\bjuin\b", 6),
            (@"\bjuli\b|\bjul[iy]?\b|\bjulio\b|\bjulho\b|\bjuillet\b", 7),
            (@"\baugust\b|\baug\b|\bagosto\b|\bao[uû]t\b", 8),
            (@"\bseptember\b|\bsep\b|\bseptiembre\b|\bsetembro\b|\bseptembre\b", 9),
            (@"\boktober\b|\boct\b|\boctober\b|\boctubre\b|\boutubro\b|\boctobre\b", 10),
            (@"\bnovember\b|\bnov\b|\bnoviembre\b|\bnovembro\b|\bnovembre\b", 11),
            (@"\bdesember\b|\bdec\b|\bdecember\b|\bdiciembre\b|\bdezembro\b|\bd[eé]cembre\b", 12),
        };

        var found = new List<int>();
        foreach (var (pattern, month) in monthPatterns)
        {
            if (Regex.IsMatch(lower, pattern))
                found.Add(month);
        }

        if (found.Count >= 2)
            return (found[0], found[1]);
        if (found.Count == 1)
        {
            // If only one month found, assume comparing with previous month
            var m = found[0];
            return (m == 1 ? 12 : m - 1, m);
        }

        // Default to Jan vs Feb
        return (1, 2);
    }
}
