using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles "set_fixed_price" tasks — finds an existing project by name and updates it
/// to have a fixed price. Requires only 2 API calls: GET (search) + PUT (update).
/// </summary>
public class FixedPriceProjectHandler : ITaskHandler
{
    private readonly ILogger<FixedPriceProjectHandler> _logger;

    public FixedPriceProjectHandler(ILogger<FixedPriceProjectHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var project = extracted.Entities.GetValueOrDefault("project") ?? new();
        var customer = extracted.Entities.GetValueOrDefault("customer") ?? new();

        // Extract project name
        var projectName = GetString(project, "name") ?? GetString(project, "projectName");
        if (string.IsNullOrEmpty(projectName))
        {
            _logger.LogWarning("set_fixed_price: no project name in extraction");
            return HandlerResult.Empty;
        }

        // Extract fixed price amount — try project entity first, then raw_amounts
        decimal fixedPrice = GetDecimal(project, "fixedPrice") ?? GetDecimal(project, "fixedprice")
            ?? GetDecimal(project, "price") ?? GetDecimal(project, "amount") ?? 0m;

        if (fixedPrice == 0m && extracted.RawAmounts.Count > 0 &&
            decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var raw))
            fixedPrice = raw;

        if (fixedPrice <= 0m)
        {
            _logger.LogWarning("set_fixed_price: could not extract a fixed price amount");
            return HandlerResult.Empty;
        }

        // Customer info for filtering if multiple projects match
        var customerName = GetString(customer, "name")
            ?? extracted.Relationships.GetValueOrDefault("customer")
            ?? GetString(project, "customerName");
        var customerOrgNumber = GetString(customer, "organizationNumber")
            ?? GetString(customer, "orgNumber")
            ?? GetString(project, "customerOrgNumber");

        // Search for project by name — get all fields needed for PUT in one call
        var searchResult = await api.GetAsync("/project", new Dictionary<string, string>
        {
            ["name"] = projectName,
            ["count"] = "10",
            ["fields"] = "id,version,name,startDate,endDate,customer(id,name,organizationNumber),isFixedPrice,fixedprice,description,projectManager(id)"
        });

        if (!searchResult.TryGetProperty("values", out var projects) || projects.GetArrayLength() == 0)
        {
            _logger.LogWarning("set_fixed_price: no project found with name '{Name}'", projectName);
            return HandlerResult.Empty;
        }

        // Find best match — prefer exact name match, then filter by customer
        JsonElement? bestMatch = null;
        foreach (var p in projects.EnumerateArray())
        {
            var pName = p.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(pName, projectName, StringComparison.OrdinalIgnoreCase)) continue;

            if (bestMatch == null)
            {
                bestMatch = p;
                continue;
            }

            // Prefer the one matching customer name/org
            if (customerOrgNumber != null && p.TryGetProperty("customer", out var cust)
                && cust.TryGetProperty("organizationNumber", out var on)
                && on.GetString() == customerOrgNumber)
            {
                bestMatch = p;
                break;
            }
            if (customerName != null && p.TryGetProperty("customer", out var cust2)
                && cust2.TryGetProperty("name", out var cn)
                && cn.GetString()?.Contains(customerName, StringComparison.OrdinalIgnoreCase) == true)
            {
                bestMatch = p;
                break;
            }
        }

        if (bestMatch == null)
        {
            // No exact name match — take the first result
            bestMatch = projects[0];
        }

        var match = bestMatch.Value;
        var projectId = match.GetProperty("id").GetInt64();
        var version = match.GetProperty("version").GetInt32();

        // Build PUT body — preserve all existing fields, add/override fixed price
        var body = new Dictionary<string, object>
        {
            ["id"] = projectId,
            ["version"] = version,
            ["name"] = match.TryGetProperty("name", out var nm) ? (object)(nm.GetString() ?? projectName) : projectName,
            ["isFixedPrice"] = true,
            ["fixedprice"] = fixedPrice
        };

        // Preserve required startDate
        if (match.TryGetProperty("startDate", out var sd) && sd.ValueKind != JsonValueKind.Null)
            body["startDate"] = sd.GetString()!;
        else
            body["startDate"] = DateTime.Today.ToString("yyyy-MM-dd");

        // Preserve optional fields
        if (match.TryGetProperty("endDate", out var ed) && ed.ValueKind != JsonValueKind.Null && ed.GetString() != null)
            body["endDate"] = ed.GetString()!;
        if (match.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null && desc.GetString() != null)
            body["description"] = desc.GetString()!;
        if (match.TryGetProperty("customer", out var custEl) && custEl.ValueKind == JsonValueKind.Object
            && custEl.TryGetProperty("id", out var custId))
            body["customer"] = new Dictionary<string, object> { ["id"] = custId.GetInt64() };
        if (match.TryGetProperty("projectManager", out var pm) && pm.ValueKind == JsonValueKind.Object
            && pm.TryGetProperty("id", out var pmId))
            body["projectManager"] = new Dictionary<string, object> { ["id"] = pmId.GetInt64() };

        var putResult = await api.PutAsync($"/project/{projectId}", body);
        _logger.LogInformation("Updated project {Id} '{Name}' fixedprice={Price}", projectId, projectName, fixedPrice);

        return new HandlerResult
        {
            EntityType = "project",
            EntityId = projectId,
            Metadata = new Dictionary<string, string>
            {
                ["fixedPrice"] = fixedPrice.ToString(CultureInfo.InvariantCulture),
                ["projectName"] = projectName
            }
        };
    }

    private static string? GetString(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        return val is JsonElement je ? je.GetString() : val?.ToString();
    }

    private static decimal? GetDecimal(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetDecimal();
            if (je.ValueKind == JsonValueKind.String &&
                decimal.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        }
        if (val is decimal dv) return dv;
        if (decimal.TryParse(val?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2)) return d2;
        return null;
    }
}
