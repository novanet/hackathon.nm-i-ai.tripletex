using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class ProjectHandler : ITaskHandler
{
    private readonly ILogger<ProjectHandler> _logger;

    public ProjectHandler(ILogger<ProjectHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var project = extracted.Entities.GetValueOrDefault("project") ?? new();

        var body = new Dictionary<string, object>();

        SetIfPresent(body, project, "name");
        SetIfPresent(body, project, "number");
        SetIfPresent(body, project, "description");
        SetIfPresent(body, project, "startDate");
        SetIfPresent(body, project, "endDate");
        SetIfPresent(body, project, "reference");

        if (!body.ContainsKey("name"))
            body["name"] = "Prosjekt";

        // Use dates from extraction if not in entity
        if (!body.ContainsKey("startDate") && extracted.Dates.Count > 0)
            body["startDate"] = extracted.Dates[0];
        if (!body.ContainsKey("endDate") && extracted.Dates.Count > 1)
            body["endDate"] = extracted.Dates[1];

        // Set boolean flags
        if (project.TryGetValue("isInternal", out var isInt))
            body["isInternal"] = bool.Parse(isInt.ToString()!);
        if (project.TryGetValue("isFixedPrice", out var isFp))
            body["isFixedPrice"] = bool.Parse(isFp.ToString()!);

        // Resolve customer if referenced
        var customerName = extracted.Relationships.GetValueOrDefault("customer")
            ?? GetStringField(project, "customer");
        if (customerName != null)
        {
            var customerId = await ResolveCustomerId(api, customerName);
            if (customerId.HasValue)
                body["customer"] = new { id = customerId.Value };
        }

        // Resolve project manager
        var managerName = extracted.Relationships.GetValueOrDefault("projectManager")
            ?? GetStringField(project, "projectManager");
        if (managerName != null)
        {
            var managerId = await ResolveEmployeeId(api, managerName);
            if (managerId.HasValue)
                body["projectManager"] = new { id = managerId.Value };
        }

        // Resolve department
        var deptName = extracted.Relationships.GetValueOrDefault("department")
            ?? GetStringField(project, "department");
        if (deptName != null)
        {
            var deptId = await ResolveDepartmentId(api, deptName);
            if (deptId.HasValue)
                body["department"] = new { id = deptId.Value };
        }

        _logger.LogInformation("Creating project: {Name}", body.GetValueOrDefault("name"));

        var result = await api.PostAsync("/project", body);
        var projectId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created project ID: {Id}", projectId);
    }

    private async Task<long?> ResolveCustomerId(TripletexApiClient api, string name)
    {
        var result = await api.GetAsync("/customer", new Dictionary<string, string>
        {
            ["name"] = name,
            ["count"] = "1",
            ["fields"] = "id,name"
        });
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
                return v.GetProperty("id").GetInt64();
        }
        return null;
    }

    private async Task<long?> ResolveEmployeeId(TripletexApiClient api, string name)
    {
        var parts = name.Split(' ', 2);
        var query = new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" };
        if (parts.Length >= 2)
        {
            query["firstName"] = parts[0];
            query["lastName"] = parts[1];
        }
        else
        {
            query["firstName"] = name;
        }
        var result = await api.GetAsync("/employee", query);
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
                return v.GetProperty("id").GetInt64();
        }
        return null;
    }

    private async Task<long?> ResolveDepartmentId(TripletexApiClient api, string name)
    {
        var result = await api.GetAsync("/department", new Dictionary<string, string>
        {
            ["name"] = name,
            ["count"] = "1",
            ["fields"] = "id,name"
        });
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
                return v.GetProperty("id").GetInt64();
        }
        return null;
    }

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
            return val is JsonElement je ? je.GetString() : val.ToString();
        return null;
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
            body[key] = val is JsonElement je ? je.ToString() : val;
    }
}
