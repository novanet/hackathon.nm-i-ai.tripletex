using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class EmployeeHandler : ITaskHandler
{
    private readonly ILogger<EmployeeHandler> _logger;

    public EmployeeHandler(ILogger<EmployeeHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var emp = extracted.Entities.GetValueOrDefault("employee") ?? new();
        var action = extracted.Action;

        if (action == "update")
        {
            await HandleUpdate(api, emp);
            return;
        }

        // Build employee body
        var body = new Dictionary<string, object>();

        // Handle name splitting: if "name" is provided but no firstName/lastName, split it
        if (!emp.ContainsKey("firstName") && !emp.ContainsKey("lastName") && emp.TryGetValue("name", out var nameObj))
        {
            var fullName = (nameObj is JsonElement nameJe ? nameJe.GetString() : nameObj?.ToString()) ?? "";
            var parts2 = fullName.Trim().Split(' ', 2);
            emp["firstName"] = parts2[0];
            emp["lastName"] = parts2.Length > 1 ? parts2[1] : parts2[0];
        }

        SetIfPresent(body, emp, "firstName");
        SetIfPresent(body, emp, "lastName");
        SetIfPresent(body, emp, "email");
        SetIfPresent(body, emp, "dateOfBirth");
        SetIfPresent(body, emp, "phoneNumberMobile");
        SetIfPresent(body, emp, "nationalIdentityNumber");
        SetIfPresent(body, emp, "bankAccountNumber");

        // Determine if admin role is needed
        var hasRoles = emp.TryGetValue("roles", out var rolesObj);
        var roles = ParseStringList(rolesObj);
        var needsAdmin = roles.Any(r => r.Equals("administrator", StringComparison.OrdinalIgnoreCase)
            || r.Equals("admin", StringComparison.OrdinalIgnoreCase));

        body["userType"] = needsAdmin ? "EXTENDED" : "STANDARD";

        // Handle department — required by Tripletex API
        long? deptId = null;
        if (extracted.Relationships.TryGetValue("department", out var deptName) && !string.IsNullOrEmpty(deptName))
        {
            var deptResult = await api.GetAsync("/department", new Dictionary<string, string>
            {
                ["query"] = deptName,
                ["count"] = "1",
                ["fields"] = "id"
            });
            if (deptResult.TryGetProperty("values", out var depts) && depts.GetArrayLength() > 0)
            {
                deptId = depts[0].GetProperty("id").GetInt64();
            }
        }

        // Fallback: department.id is required, use first available
        if (deptId == null)
        {
            var allDepts = await api.GetAsync("/department", new Dictionary<string, string>
            {
                ["count"] = "1",
                ["fields"] = "id"
            });
            if (allDepts.TryGetProperty("values", out var defaultDepts) && defaultDepts.GetArrayLength() > 0)
            {
                deptId = defaultDepts[0].GetProperty("id").GetInt64();
            }
        }

        if (deptId != null)
        {
            body["department"] = new { id = deptId.Value };
        }

        _logger.LogInformation("Creating employee: {FirstName} {LastName}",
            body.GetValueOrDefault("firstName"), body.GetValueOrDefault("lastName"));

        var result = await api.PostAsync("/employee", body);
        var employeeId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created employee ID: {Id}", employeeId);

        // Assign administrator role if needed
        if (needsAdmin)
        {
            _logger.LogInformation("Assigning administrator role to employee {Id}", employeeId);
            await api.PutAsync(
                "/employee/entitlement/:grantEntitlementsByTemplate",
                body: null,
                queryParams: new Dictionary<string, string>
                {
                    ["employeeId"] = employeeId.ToString(),
                    ["template"] = "ALL_PRIVILEGES"
                });
        }
    }

    private async Task HandleUpdate(TripletexApiClient api, Dictionary<string, object> emp)
    {
        // Handle name splitting for search
        if (!emp.ContainsKey("firstName") && !emp.ContainsKey("lastName") && emp.TryGetValue("name", out var nameObj2))
        {
            var fullName = (nameObj2 is JsonElement nameJe2 ? nameJe2.GetString() : nameObj2?.ToString()) ?? "";
            var parts2 = fullName.Trim().Split(' ', 2);
            emp["firstName"] = parts2[0];
            emp["lastName"] = parts2.Length > 1 ? parts2[1] : parts2[0];
        }

        // Find the employee first
        var searchParams = new Dictionary<string, string> { ["count"] = "100", ["fields"] = "*" };
        if (emp.TryGetValue("firstName", out var fn))
            searchParams["firstName"] = fn.ToString()!;
        if (emp.TryGetValue("lastName", out var ln))
            searchParams["lastName"] = ln.ToString()!;
        if (emp.TryGetValue("email", out var email))
            searchParams["email"] = email.ToString()!;

        var searchResult = await api.GetAsync("/employee", searchParams);
        var employees = searchResult.GetProperty("values");
        if (employees.GetArrayLength() == 0)
        {
            _logger.LogWarning("No employee found to update");
            return;
        }

        var existing = employees[0];
        var id = existing.GetProperty("id").GetInt64();
        var version = existing.GetProperty("version").GetInt64();

        // Start from the existing employee data so all required fields are included
        var updateBody = new Dictionary<string, object>();
        // Only include core required fields from existing + changed fields
        var requiredFields = new HashSet<string> { "id", "version", "firstName", "lastName", "dateOfBirth", "userType", "email" };
        foreach (var prop in existing.EnumerateObject())
        {
            if (!requiredFields.Contains(prop.Name)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Null) continue;
            updateBody[prop.Name] = prop.Value;
        }
        // Include department reference if present
        if (existing.TryGetProperty("department", out var dept) && dept.ValueKind == JsonValueKind.Object)
            updateBody["department"] = dept;

        // dateOfBirth is required on PUT — default if employee was created without it
        if (!updateBody.ContainsKey("dateOfBirth"))
            updateBody["dateOfBirth"] = "1990-01-01";

        // Field name remapping for common LLM extraction aliases
        var fieldAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["phoneNumber"] = "phoneNumberMobile",
            ["phone"] = "phoneNumberMobile",
            ["telefon"] = "phoneNumberMobile",
            ["telefonnummer"] = "phoneNumberMobile",
            ["mobilnummer"] = "phoneNumberMobile",
            ["name"] = "firstName",
        };

        // Override with updated fields
        foreach (var (key, value) in emp)
        {
            if (key is "firstName" or "lastName" or "name") continue;
            var mappedKey = fieldAliases.TryGetValue(key, out var alias) ? alias : key;
            // Clean phone numbers — strip spaces and country prefix for Tripletex
            if (mappedKey == "phoneNumberMobile")
            {
                var phone = (value is JsonElement pje ? pje.GetString() : value?.ToString()) ?? "";
                phone = phone.Replace(" ", "").Replace("'", "");
                if (phone.StartsWith("+47")) phone = phone[3..];
                updateBody[mappedKey] = phone;
            }
            else
            {
                updateBody[mappedKey] = value;
            }
        }

        await api.PutAsync($"/employee/{id}", updateBody);
        _logger.LogInformation("Updated employee ID: {Id}", id);
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
        {
            body[key] = val is JsonElement je ? je.ToString() : val;
        }
    }

    private static List<string> ParseStringList(object? val)
    {
        if (val is null) return new();
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            if (je.ValueKind == JsonValueKind.String)
                return new List<string> { je.GetString()! };
        }
        if (val is string s) return new List<string> { s };
        if (val is IEnumerable<object> list) return list.Select(x => x.ToString() ?? "").ToList();
        if (val is IEnumerable<string> strList) return strList.ToList();
        return new();
    }
}
