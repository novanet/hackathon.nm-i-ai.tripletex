using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class DepartmentHandler : ITaskHandler
{
    private readonly ILogger<DepartmentHandler> _logger;

    public DepartmentHandler(ILogger<DepartmentHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var dept = extracted.Entities.GetValueOrDefault("department") ?? new();
        var handlerResult = new HandlerResult { EntityType = "department" };

        // Handle multi-department creation: LLM may return "names": ["A", "B", "C"]
        var names = new List<string>();
        if (dept.TryGetValue("names", out var namesVal) && namesVal is JsonElement namesArr && namesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in namesArr.EnumerateArray())
                if (n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString()))
                    names.Add(n.GetString()!);
        }
        if (names.Count == 0 && dept.TryGetValue("name", out var nameVal) && nameVal is not null)
        {
            var n = nameVal is JsonElement je ? je.ToString() : nameVal.ToString();
            if (!string.IsNullOrEmpty(n)) names.Add(n!);
        }

        // Also check for multiple department entities (department1, department2, ...)
        if (names.Count == 0)
        {
            foreach (var kvp in extracted.Entities)
            {
                if (kvp.Key.StartsWith("department") && kvp.Value.TryGetValue("name", out var dn) && dn is not null)
                {
                    var n = dn is JsonElement je2 ? je2.ToString() : dn.ToString();
                    if (!string.IsNullOrEmpty(n)) names.Add(n!);
                }
            }
        }

        // Resolve department manager if specified (shared across all departments)
        object? managerRef = null;
        if (dept.TryGetValue("departmentManager", out var mgrName) && mgrName is not null)
        {
            var mgrStr = mgrName is JsonElement je
                ? (je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText())
                : mgrName.ToString();
            if (!string.IsNullOrEmpty(mgrStr))
            {
                var parts = mgrStr!.Split(' ', 2);
                var searchParams = new Dictionary<string, string>
                {
                    ["firstName"] = parts[0],
                    ["count"] = "1",
                    ["fields"] = "id"
                };
                if (parts.Length > 1)
                    searchParams["lastName"] = parts[1];

                var empResult = await api.GetAsync("/employee", searchParams);
                if (empResult.TryGetProperty("values", out var emps) && emps.GetArrayLength() > 0)
                    managerRef = new { id = emps[0].GetProperty("id").GetInt64() };
            }
        }

        var deptNumber = 1;

        if (dept.TryGetValue("departmentNumber", out var dnVal) && dnVal is not null)
        {
            var dnStr = dnVal is JsonElement dje ? dje.ToString() : dnVal.ToString();
            int.TryParse(dnStr, out deptNumber);
        }

        // Batch creation: use POST /department/list when creating multiple departments (saves N-1 writes)
        if (names.Count > 1)
        {
            var batchItems = new List<Dictionary<string, object>>();
            foreach (var name in names)
            {
                var item = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["departmentNumber"] = deptNumber++
                };
                if (managerRef is not null)
                    item["departmentManager"] = managerRef;
                batchItems.Add(item);
            }

            _logger.LogInformation("Batch-creating {Count} departments via POST /department/list", batchItems.Count);
            var batchResult = await api.PostAsync("/department/list", batchItems);
            if (batchResult.TryGetProperty("values", out var batchVals))
            {
                foreach (var v in batchVals.EnumerateArray())
                {
                    var deptId = v.GetProperty("id").GetInt64();
                    if (handlerResult.EntityId == null)
                        handlerResult.EntityId = deptId;
                    else
                        handlerResult.AdditionalEntityIds.Add(deptId);
                }
            }
            return handlerResult;
        }

        // Single department: optimistic POST directly (no pre-fetch needed in clean competition env)
        var usedNumbers = new HashSet<int>();

        foreach (var name in names)
        {
            var body = new Dictionary<string, object> { ["name"] = name };
            if (managerRef is not null)
                body["departmentManager"] = managerRef;

            // Find next unoccupied number
            while (usedNumbers.Contains(deptNumber))
                deptNumber++;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                body["departmentNumber"] = deptNumber;
                try
                {
                    _logger.LogInformation("Creating department: {Name} (number {Num})", name, deptNumber);
                    var result = await api.PostAsync("/department", body);
                    var deptId = result.GetProperty("value").GetProperty("id").GetInt64();
                    _logger.LogInformation("Created department ID: {Id}", deptId);
                    usedNumbers.Add(deptNumber);
                    deptNumber++;
                    if (handlerResult.EntityId == null)
                        handlerResult.EntityId = deptId;
                    else
                        handlerResult.AdditionalEntityIds.Add(deptId);
                    break;
                }
                catch (TripletexApiException ex) when (ex.Message.Contains("Nummeret er i bruk") || ex.Message.Contains("nummer") || ex.Message.Contains("duplicate"))
                {
                    _logger.LogWarning("Department number {Num} in use, trying next", deptNumber);
                    deptNumber++;
                    body["departmentNumber"] = deptNumber;
                    if (attempt == 4) throw;
                }
            }
        }
        return handlerResult;
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
        {
            body[key] = val is JsonElement je ? je.ToString() : val;
        }
    }
}
