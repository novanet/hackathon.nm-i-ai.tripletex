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
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingNumbers = new HashSet<int>();

        // Always query existing departments to avoid number collisions (each 4xx hurts efficiency score)
        try
        {
            var existing = await api.GetAsync("/department", new Dictionary<string, string>
            {
                ["from"] = "0",
                ["count"] = "1000",
                ["fields"] = "departmentNumber,name"
            });
            if (existing.TryGetProperty("values", out var vals))
            {
                foreach (var d in vals.EnumerateArray())
                {
                    if (d.TryGetProperty("departmentNumber", out var dn) && dn.TryGetInt32(out var n))
                        existingNumbers.Add(n);
                    if (d.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                        existingNames.Add(nm.GetString()!);
                }
            }
        }
        catch { /* fallback */ }

        if (dept.TryGetValue("departmentNumber", out var dnVal) && dnVal is not null)
        {
            var dnStr = dnVal is JsonElement dje ? dje.ToString() : dnVal.ToString();
            int.TryParse(dnStr, out deptNumber);
        }

        // Find first available number at or above deptNumber
        while (existingNumbers.Contains(deptNumber))
            deptNumber++;

        foreach (var name in names)
        {
            // Skip if department with this name already exists
            if (existingNames.Contains(name))
            {
                _logger.LogInformation("Department '{Name}' already exists, skipping", name);
                continue;
            }

            var body = new Dictionary<string, object> { ["name"] = name };
            if (managerRef is not null)
                body["departmentManager"] = managerRef;

            // Try creating with auto-assigned number, retry on collision (pre-query handles known numbers)
            for (var attempt = 0; attempt < 3; attempt++)
            {
                body["departmentNumber"] = deptNumber++;
                try
                {
                    _logger.LogInformation("Creating department: {Name} (number {Num})", name, (int)body["departmentNumber"]);
                    var result = await api.PostAsync("/department", body);
                    var deptId = result.GetProperty("value").GetProperty("id").GetInt64();
                    _logger.LogInformation("Created department ID: {Id}", deptId);
                    if (handlerResult.EntityId == null)
                        handlerResult.EntityId = deptId;
                    else
                        handlerResult.AdditionalEntityIds.Add(deptId);
                    break;
                }
                catch (TripletexApiException ex) when (ex.Message.Contains("Nummeret er i bruk"))
                {
                    _logger.LogWarning("Department number {Num} in use, trying next", (int)body["departmentNumber"]);
                    if (attempt == 2) throw;
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
