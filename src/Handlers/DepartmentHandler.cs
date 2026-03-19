using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class DepartmentHandler : ITaskHandler
{
    private readonly ILogger<DepartmentHandler> _logger;

    public DepartmentHandler(ILogger<DepartmentHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var dept = extracted.Entities.GetValueOrDefault("department") ?? new();

        var body = new Dictionary<string, object>();
        SetIfPresent(body, dept, "name");
        SetIfPresent(body, dept, "departmentNumber");

        // Resolve department manager if specified
        if (dept.TryGetValue("departmentManager", out var mgrName) && mgrName is not null)
        {
            var mgrStr = mgrName is JsonElement je ? je.GetString() : mgrName.ToString();
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
                    body["departmentManager"] = new { id = emps[0].GetProperty("id").GetInt32() };
            }
        }

        _logger.LogInformation("Creating department: {Name}", body.GetValueOrDefault("name"));

        var result = await api.PostAsync("/department", body);
        var deptId = result.GetProperty("value").GetProperty("id").GetInt32();

        _logger.LogInformation("Created department ID: {Id}", deptId);
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
        {
            body[key] = val is JsonElement je ? je.ToString() : val;
        }
    }
}
