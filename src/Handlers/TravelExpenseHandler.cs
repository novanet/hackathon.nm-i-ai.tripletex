using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class TravelExpenseHandler : ITaskHandler
{
    private readonly ILogger<TravelExpenseHandler> _logger;

    public TravelExpenseHandler(ILogger<TravelExpenseHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        if (extracted.Action == "delete")
        {
            await HandleDelete(api, extracted);
            return HandlerResult.Empty;
        }

        var travel = extracted.Entities.GetValueOrDefault("travelExpense")
            ?? extracted.Entities.GetValueOrDefault("travel_expense")
            ?? new();

        // Step 1: Resolve employee
        var employeeName = extracted.Relationships.GetValueOrDefault("employee")
            ?? GetStringField(travel, "employee");
        long? employeeId = null;
        if (employeeName != null)
            employeeId = await ResolveEmployeeId(api, employeeName);

        // If no employee found, get first available
        if (!employeeId.HasValue)
        {
            var empResult = await api.GetAsync("/employee", new Dictionary<string, string>
            {
                ["count"] = "1",
                ["fields"] = "id"
            });
            if (empResult.TryGetProperty("values", out var vals))
            {
                foreach (var v in vals.EnumerateArray())
                {
                    employeeId = v.GetProperty("id").GetInt64();
                    break;
                }
            }
        }

        // Step 2: Build travel expense body
        var body = new Dictionary<string, object>();
        if (employeeId.HasValue)
            body["employee"] = new { id = employeeId.Value };

        SetIfPresent(body, travel, "title");
        if (!body.ContainsKey("title"))
            body["title"] = "Reiseregning";

        // Build travelDetails
        var details = extracted.Entities.GetValueOrDefault("travelDetails") ?? new();
        var travelDetails = new Dictionary<string, object>();

        SetIfPresent(travelDetails, details, "departureDate");
        SetIfPresent(travelDetails, details, "returnDate");
        SetIfPresent(travelDetails, details, "departureFrom");
        SetIfPresent(travelDetails, details, "destination");
        SetIfPresent(travelDetails, details, "departureTime");
        SetIfPresent(travelDetails, details, "returnTime");
        SetIfPresent(travelDetails, details, "purpose");

        // Also check if these fields are on travel entity itself
        SetIfPresent(travelDetails, travel, "departureDate");
        SetIfPresent(travelDetails, travel, "returnDate");
        SetIfPresent(travelDetails, travel, "departureFrom");
        SetIfPresent(travelDetails, travel, "destination");
        SetIfPresent(travelDetails, travel, "departureTime");
        SetIfPresent(travelDetails, travel, "returnTime");
        SetIfPresent(travelDetails, travel, "purpose");

        // Use extracted dates as fallback
        if (!travelDetails.ContainsKey("departureDate") && extracted.Dates.Count > 0)
            travelDetails["departureDate"] = extracted.Dates[0];
        if (!travelDetails.ContainsKey("returnDate") && extracted.Dates.Count > 1)
            travelDetails["returnDate"] = extracted.Dates[1];

        if (travelDetails.TryGetValue("departureDate", out var dd))
        {
            // isForeignTravel / isDayTrip defaults
            if (!travelDetails.ContainsKey("isForeignTravel"))
                travelDetails["isForeignTravel"] = false;
            if (!travelDetails.ContainsKey("isDayTrip"))
            {
                var isDayTrip = !travelDetails.ContainsKey("returnDate")
                    || travelDetails.GetValueOrDefault("returnDate")?.ToString() == dd.ToString();
                travelDetails["isDayTrip"] = isDayTrip;
            }
        }

        if (travelDetails.Count > 0)
            body["travelDetails"] = travelDetails;

        _logger.LogInformation("Creating travel expense: {Title}", body.GetValueOrDefault("title"));

        var result = await api.PostAsync("/travelExpense", body);
        var travelId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created travel expense ID: {Id}", travelId);

        var handlerResult = new HandlerResult { EntityType = "travelExpense", EntityId = travelId };

        // Step 3: Add cost lines if present
        var costs = extracted.Entities.GetValueOrDefault("costs") ?? new();
        var costLines = new List<Dictionary<string, object>>();

        // Also check for costs array inside the travel entity (LLM uses various key names)
        JsonElement costsArr = default;
        if (travel.TryGetValue("costs", out var costsVal) && costsVal is JsonElement ca1 && ca1.ValueKind == JsonValueKind.Array)
            costsArr = ca1;
        else if (travel.TryGetValue("cost_items", out var costItemsVal) && costItemsVal is JsonElement ca2 && ca2.ValueKind == JsonValueKind.Array)
            costsArr = ca2;
        else if (travel.TryGetValue("costLines", out var costLinesVal) && costLinesVal is JsonElement ca3 && ca3.ValueKind == JsonValueKind.Array)
            costsArr = ca3;

        if (costsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in costsArr.EnumerateArray())
            {
                var costLine = new Dictionary<string, object>();
                if (item.TryGetProperty("description", out var d)) costLine["comments"] = d.GetString()!;
                if (item.TryGetProperty("amount", out var a))
                    costLine["amountCurrencyIncVat"] = a.ValueKind == JsonValueKind.Number ? a.GetDouble() : double.Parse(a.GetString()!, CultureInfo.InvariantCulture);
                if (item.TryGetProperty("amountCurrencyIncVat", out var a2))
                    costLine["amountCurrencyIncVat"] = a2.ValueKind == JsonValueKind.Number ? a2.GetDouble() : double.Parse(a2.GetString()!, CultureInfo.InvariantCulture);
                if (item.TryGetProperty("date", out var dt)) costLine["date"] = dt.GetString()!;
                if (item.TryGetProperty("category", out var c)) costLine["category"] = c.GetString()!;
                costLines.Add(costLine);
            }
        }

        // Try to parse individual cost entries from top-level costs entity
        foreach (var (key, val) in costs)
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                var costLine = new Dictionary<string, object>();
                if (je.TryGetProperty("description", out var desc))
                    costLine["comments"] = desc.GetString()!;
                if (je.TryGetProperty("amount", out var amt))
                    costLine["amountCurrencyIncVat"] = amt.GetDouble();
                if (je.TryGetProperty("amountCurrencyIncVat", out var amt2))
                    costLine["amountCurrencyIncVat"] = amt2.GetDouble();
                if (je.TryGetProperty("date", out var date))
                    costLine["date"] = date.GetString()!;
                if (je.TryGetProperty("category", out var cat))
                    costLine["category"] = cat.GetString()!;
                costLines.Add(costLine);
            }
        }

        // If raw amounts and no cost lines parsed, create a single cost
        if (costLines.Count == 0 && extracted.RawAmounts.Count > 0)
        {
            if (decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            {
                costLines.Add(new Dictionary<string, object>
                {
                    ["amountCurrencyIncVat"] = amount,
                    ["date"] = extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd"),
                    ["category"] = GetStringField(travel, "costCategory") ?? "Annet"
                });
            }
        }

        // Post each cost line
        foreach (var costLine in costLines)
        {
            costLine["travelExpense"] = new { id = travelId };

            // Resolve payment type if not set
            if (!costLine.ContainsKey("paymentType"))
            {
                var ptId = await ResolvePaymentTypeId(api);
                if (ptId.HasValue)
                    costLine["paymentType"] = new { id = ptId.Value };
            }

            _logger.LogInformation("Adding cost to travel expense {TravelId}", travelId);
            await api.PostAsync("/travelExpense/cost", costLine);
        }
        return handlerResult;
    }

    private async Task HandleDelete(TripletexApiClient api, ExtractionResult extracted)
    {
        var travel = extracted.Entities.GetValueOrDefault("travelExpense")
            ?? extracted.Entities.GetValueOrDefault("travel_expense")
            ?? new();
        var idStr = GetStringField(travel, "id");

        if (idStr != null && long.TryParse(idStr, out var travelId))
        {
            _logger.LogInformation("Deleting travel expense ID: {Id}", travelId);
            await api.DeleteAsync($"/travelExpense/{travelId}");
            return;
        }

        // Search by employee/title
        var employeeName = extracted.Relationships.GetValueOrDefault("employee")
            ?? GetStringField(travel, "employee");
        var query = new Dictionary<string, string> { ["count"] = "100", ["fields"] = "id,title" };

        if (employeeName != null)
        {
            var empId = await ResolveEmployeeId(api, employeeName);
            if (empId.HasValue)
                query["employeeId"] = empId.Value.ToString();
        }

        var result = await api.GetAsync("/travelExpense", query);
        if (result.TryGetProperty("values", out var vals))
        {
            var title = GetStringField(travel, "title");
            foreach (var v in vals.EnumerateArray())
            {
                if (title != null)
                {
                    var vTitle = v.TryGetProperty("title", out var t) ? t.GetString() : "";
                    if (vTitle != title) continue;
                }
                var id = v.GetProperty("id").GetInt64();
                _logger.LogInformation("Deleting travel expense ID: {Id}", id);
                await api.DeleteAsync($"/travelExpense/{id}");
                return;
            }
        }

        _logger.LogWarning("Could not find travel expense to delete");
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

    private async Task<long?> ResolvePaymentTypeId(TripletexApiClient api)
    {
        var result = await api.GetAsync("/travelExpense/paymentType", new Dictionary<string, string>
        {
            ["count"] = "10",
            ["fields"] = "id,description"
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
        {
            if (val is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.String => je.GetString(),
                    JsonValueKind.Number => je.GetRawText(),
                    JsonValueKind.Object => je.TryGetProperty("name", out var n) ? n.GetString() : je.GetRawText(),
                    _ => je.GetRawText()
                };
            }
            return val.ToString();
        }
        return null;
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null && !body.ContainsKey(key))
            body[key] = val is JsonElement je ? je.ToString() : val;
    }
}
