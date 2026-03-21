using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class EmployeeHandler : ITaskHandler
{
    private readonly ILogger<EmployeeHandler> _logger;

    public EmployeeHandler(ILogger<EmployeeHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var emp = extracted.Entities.GetValueOrDefault("employee") ?? new();
        var action = extracted.Action;

        if (action == "update")
        {
            await HandleUpdate(api, emp);
            return HandlerResult.Empty;
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

        // Generate synthetic email when missing (e.g. PDF offer letters rarely include one).
        // STANDARD userType requires email — without it we get a 422.
        if (!body.ContainsKey("email"))
        {
            var fn = (body.TryGetValue("firstName", out var fnv) ? fnv?.ToString() : null) ?? "employee";
            var ln = (body.TryGetValue("lastName", out var lnv) ? lnv?.ToString() : null) ?? "noreply";
            var syntheticEmail = $"{fn.ToLowerInvariant().Replace(" ", ".")}.{ln.ToLowerInvariant().Replace(" ", ".")}@example.org";
            body["email"] = syntheticEmail;
            _logger.LogInformation("No email extracted — using synthetic: {Email}", syntheticEmail);
        }

        SetIfPresent(body, emp, "dateOfBirth");
        SetIfPresent(body, emp, "phoneNumberMobile");
        SetIfPresent(body, emp, "nationalIdentityNumber");
        SetIfPresent(body, emp, "bankAccountNumber");

        // dateOfBirth is required by the API — provide a default if not in prompt
        if (!body.ContainsKey("dateOfBirth"))
            body["dateOfBirth"] = "1990-01-01";

        // Email is required for STANDARD and EXTENDED userType. If not extracted (e.g. PDF offer letters
        // without email), generate a synthetic one from the employee name to avoid 422 errors.
        if (!body.ContainsKey("email"))
        {
            var fn2 = (body.GetValueOrDefault("firstName")?.ToString() ?? "user").ToLowerInvariant().Replace(" ", "");
            var ln2 = (body.GetValueOrDefault("lastName")?.ToString() ?? "tripletex").ToLowerInvariant().Replace(" ", "");
            body["email"] = $"{fn2}.{ln2}@example.org";
            _logger.LogInformation("No email extracted — using synthetic email: {Email}", body["email"]);
        }

        // Extract startDate for employment (separate API object)
        string? startDate = null;
        if (emp.TryGetValue("startDate", out var sdObj))
            startDate = (sdObj is JsonElement sdJe ? sdJe.GetString() : sdObj?.ToString());

        // Determine if admin role is needed — check multiple extraction patterns
        var hasRoles = emp.TryGetValue("roles", out var rolesObj);
        var roles = ParseStringList(rolesObj);
        var needsAdmin = roles.Any(r => r.Equals("administrator", StringComparison.OrdinalIgnoreCase)
            || r.Equals("admin", StringComparison.OrdinalIgnoreCase));

        // Fallback: check entity fields the LLM may use instead of roles array
        if (!needsAdmin)
        {
            // Check for isAdmin/isAdministrator boolean fields
            foreach (var key in new[] { "isAdmin", "isAdministrator", "admin", "role", "access", "userType" })
            {
                if (emp.TryGetValue(key, out var v) && v is not null)
                {
                    var s = v is JsonElement je2 ? je2.ToString() : v.ToString();
                    if (s != null && (s.Contains("admin", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || s.Contains("EXTENDED", StringComparison.OrdinalIgnoreCase)))
                    {
                        needsAdmin = true;
                        break;
                    }
                }
            }
        }

        // Fallback: check raw prompt for admin keywords in any language
        if (!needsAdmin && extracted.RawPrompt != null)
        {
            var prompt = extracted.RawPrompt;
            needsAdmin = prompt.Contains("admin", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("administrateur", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("Verwaltung", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("administrador", StringComparison.OrdinalIgnoreCase);
        }

        if (needsAdmin)
            _logger.LogInformation("Admin role detected for employee");

        body["userType"] = needsAdmin ? "EXTENDED" : "STANDARD";

        // Handle department — REQUIRED when department module is active (competition environments always have it).
        // Always fetch the first available department as a fallback. If a specific department name is
        // in Relationships, search for it; otherwise just take the first one.
        long? deptId = null;
        extracted.Relationships.TryGetValue("department", out var deptName);
        var deptQueryParams = new Dictionary<string, string>
        {
            ["count"] = "1",
            ["fields"] = "id"
        };
        if (!string.IsNullOrEmpty(deptName))
            deptQueryParams["query"] = deptName;

        var deptResult = await api.GetAsync("/department", deptQueryParams);
        if (deptResult.TryGetProperty("values", out var depts) && depts.GetArrayLength() > 0)
        {
            deptId = depts[0].GetProperty("id").GetInt64();
        }

        // If no department found and a specific one was searched, try without the query filter
        if (deptId == null && !string.IsNullOrEmpty(deptName))
        {
            var fallbackDeptResult = await api.GetAsync("/department", new Dictionary<string, string>
            {
                ["count"] = "1",
                ["fields"] = "id"
            });
            if (fallbackDeptResult.TryGetProperty("values", out var fallbackDepts) && fallbackDepts.GetArrayLength() > 0)
                deptId = fallbackDepts[0].GetProperty("id").GetInt64();
        }

        // If still no department, create one — required when department module is active
        if (deptId == null)
        {
            _logger.LogInformation("No department found, creating default department for employee");
            try
            {
                var newDept = await api.PostAsync("/department", new Dictionary<string, object>
                {
                    ["name"] = "Hovedavdeling",
                    ["departmentNumber"] = "1"
                });
                deptId = newDept.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created default department {Id}", deptId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create default department, trying departmentNumber 99");
                try
                {
                    var newDept = await api.PostAsync("/department", new Dictionary<string, object>
                    {
                        ["name"] = "Hovedavdeling",
                        ["departmentNumber"] = "99"
                    });
                    deptId = newDept.GetProperty("value").GetProperty("id").GetInt64();
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning(ex2, "Department creation also failed — proceeding without department");
                }
            }
        }

        if (deptId != null)
        {
            body["department"] = new Dictionary<string, object> { ["id"] = deptId.Value };
        }

        // Inline employment in employee POST body to save API calls (no separate GET /division + POST /employee/employment)
        // Competition environments don't require division; sandbox does. Fallback handles sandbox gracefully.
        if (!string.IsNullOrEmpty(startDate))
        {
            body["employments"] = new[]
            {
                new Dictionary<string, object> { ["startDate"] = startDate }
            };
        }

        _logger.LogInformation("Creating employee: {FirstName} {LastName} (inlineEmployment={HasEmployment})",
            body.GetValueOrDefault("firstName"), body.GetValueOrDefault("lastName"), !string.IsNullOrEmpty(startDate));

        JsonElement apiResult;
        try
        {
            apiResult = await api.PostAsync("/employee", body);
        }
        catch (TripletexApiException ex) when (ex.StatusCode == 422
            && body.ContainsKey("employments"))
        {
            // Inline employment rejected (sandbox requires division, or other environment constraint) — retry without employment
            _logger.LogInformation("Inline employment failed (422), retrying without employment");
            body.Remove("employments");
            apiResult = await api.PostAsync("/employee", body);
        }
        var employeeId = apiResult.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created employee ID: {Id}", employeeId);

        var result = new HandlerResult { EntityType = "employee", EntityId = employeeId };

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
            result.Metadata["adminRole"] = "true";
        }

        return result;
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
