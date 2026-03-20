using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        long? employeeId = null;

        // First try: use the employee entity with proper firstName/lastName
        var empEntity = extracted.Entities.GetValueOrDefault("employee") ?? new();
        var empFirstName = GetScalarString(empEntity, "firstName");
        var empLastName = GetScalarString(empEntity, "lastName");
        var empEmail = GetScalarString(empEntity, "email");

        // Fallback: extract nested employee object from travelExpense entity
        if (empFirstName == null && empLastName == null && empEmail == null
            && travel.TryGetValue("employee", out var nestedEmp))
        {
            if (nestedEmp is JsonElement empJe)
            {
                if (empJe.ValueKind == JsonValueKind.Object)
                {
                    if (empJe.TryGetProperty("firstName", out var fn)) empFirstName = fn.GetString();
                    if (empJe.TryGetProperty("lastName", out var ln)) empLastName = ln.GetString();
                    if (empJe.TryGetProperty("email", out var em)) empEmail = em.GetString();
                }
                else if (empJe.ValueKind == JsonValueKind.String)
                {
                    var raw = empJe.GetString();
                    if (raw != null && raw.TrimStart().StartsWith('{'))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(raw);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("firstName", out var fn2)) empFirstName = fn2.GetString();
                            if (root.TryGetProperty("lastName", out var ln2)) empLastName = ln2.GetString();
                            if (root.TryGetProperty("email", out var em2)) empEmail = em2.GetString();
                        }
                        catch { }
                    }
                    else if (raw != null)
                    {
                        var parts = raw.Split(' ', 2);
                        empFirstName = parts[0];
                        empLastName = parts.Length > 1 ? parts[1] : null;
                    }
                }
            }
            else if (nestedEmp is Dictionary<string, object> empDict)
            {
                empFirstName = GetScalarString(empDict, "firstName");
                empLastName = GetScalarString(empDict, "lastName");
                empEmail = GetScalarString(empDict, "email");
            }
            if (empFirstName != null || empLastName != null || empEmail != null)
                _logger.LogInformation("Extracted nested employee: {First} {Last} ({Email})", empFirstName, empLastName, empEmail);
        }

        // Fallback: regex from raw prompt (handles all 7 languages)
        if (empFirstName == null && empLastName == null && extracted.RawPrompt != null)
        {
            var nameMatch = Regex.Match(extracted.RawPrompt,
                @"(?:for|pour|para|für|f[oö]r)\s+([A-ZÆØÅÄÖÜ][a-zæøåäöüéèêëàáâãíìîïóòôõúùûü]+)\s+([A-ZÆØÅÄÖÜ][a-zæøåäöüéèêëàáâãíìîïóòôõúùûü]+)",
                RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (nameMatch.Success)
            {
                empFirstName = nameMatch.Groups[1].Value;
                empLastName = nameMatch.Groups[2].Value;
                _logger.LogInformation("Extracted employee from prompt regex: {First} {Last}", empFirstName, empLastName);
            }
        }
        if (empEmail == null && extracted.RawPrompt != null)
        {
            var emailMatch = Regex.Match(extracted.RawPrompt, @"[\w.+-]+@[\w.-]+\.\w+",
                RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (emailMatch.Success) empEmail = emailMatch.Value;
        }

        // Fallback: parse employeeReference / employeeName string as "FirstName LastName"
        if (empFirstName == null && empLastName == null)
        {
            var nameStr = GetStringField(travel, "employeeReference")
                ?? GetStringField(travel, "employeeName")
                ?? extracted.Relationships.GetValueOrDefault("employee");
            if (nameStr != null && !nameStr.Contains('{') && !nameStr.Contains('@'))
            {
                var parts = nameStr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) { empFirstName = parts[0]; empLastName = parts[1]; }
                else if (parts.Length == 1) { empFirstName = parts[0]; empLastName = parts[0]; }
                _logger.LogInformation("Parsed employee name from string: {First} {Last}", empFirstName, empLastName);
            }
            // Try email from travel entity or relationships
            empEmail ??= GetStringField(travel, "employeeEmail");
        }

        if (empFirstName != null && empLastName != null)
        {
            employeeId = await ResolveEmployeeByName(api, empFirstName, empLastName);
        }

        // Second try: search by email
        if (!employeeId.HasValue && empEmail != null)
        {
            employeeId = await ResolveEmployeeByEmail(api, empEmail);
        }

        // Last resort: create the employee so the expense links to the correct person
        if (!employeeId.HasValue && empFirstName != null && empLastName != null)
        {
            _logger.LogInformation("Employee not found, creating: {First} {Last}", empFirstName, empLastName);
            var empBody = new Dictionary<string, object>
            {
                ["firstName"] = empFirstName,
                ["lastName"] = empLastName,
                ["userType"] = "STANDARD",
                ["dateOfBirth"] = "1990-01-01"
            };
            if (empEmail != null) empBody["email"] = empEmail;

            // Department may be required — find first available
            try
            {
                var depts = await api.GetAsync("/department", new Dictionary<string, string>
                    { ["count"] = "1", ["fields"] = "id" });
                if (depts.TryGetProperty("values", out var dv) && dv.GetArrayLength() > 0)
                    empBody["department"] = new { id = dv[0].GetProperty("id").GetInt64() };
            }
            catch { /* department lookup optional */ }

            var empResult = await api.PostAsync("/employee", empBody);
            employeeId = empResult.GetProperty("value").GetProperty("id").GetInt64();
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
        else if (travel.TryGetValue("costItems", out var costItemsVal) && costItemsVal is JsonElement ca2 && ca2.ValueKind == JsonValueKind.Array)
            costsArr = ca2;
        else if (travel.TryGetValue("cost_items", out var costItems2Val) && costItems2Val is JsonElement ca2b && ca2b.ValueKind == JsonValueKind.Array)
            costsArr = ca2b;
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

        // Generate per diem / daily allowance cost line from travelDetails
        var travelDetailsSrc = extracted.Entities.GetValueOrDefault("travelDetails") ?? new();
        // Also check inside the travel entity
        var durationStr = GetStringField(travelDetailsSrc, "durationDays") ?? GetStringField(travel, "durationDays");
        var rateStr = GetStringField(travelDetailsSrc, "dailyAllowanceRate")
            ?? GetStringField(travelDetailsSrc, "perDiemRate")
            ?? GetStringField(travelDetailsSrc, "dailyRate")
            ?? GetStringField(travel, "dailyAllowanceRate")
            ?? GetStringField(travel, "perDiemRate")
            ?? GetStringField(travel, "dailyRate");
        if (travel.TryGetValue("travelDetails", out var tdVal) && tdVal is JsonElement tdElem && tdElem.ValueKind == JsonValueKind.Object)
        {
            if (durationStr == null && tdElem.TryGetProperty("durationDays", out var dd2))
                durationStr = dd2.GetRawText();
            if (rateStr == null)
            {
                if (tdElem.TryGetProperty("dailyAllowanceRate", out var dr2))
                    rateStr = dr2.GetRawText();
                else if (tdElem.TryGetProperty("perDiemRate", out var pr2))
                    rateStr = pr2.GetRawText();
                else if (tdElem.TryGetProperty("dailyRate", out var dlr2))
                    rateStr = dlr2.GetRawText();
            }
        }

        if (durationStr != null && rateStr != null
            && double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var days)
            && double.TryParse(rateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
        {
            var perDiemAmount = days * rate;
            _logger.LogInformation("Adding per diem cost: {Days} days x {Rate} = {Total}", days, rate, perDiemAmount);
            costLines.Add(new Dictionary<string, object>
            {
                ["amountCurrencyIncVat"] = perDiemAmount,
                ["comments"] = "Diett",
                ["date"] = extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd")
            });
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

        // Pre-resolve payment type once (cached for all cost lines)
        long? cachedPaymentTypeId = await ResolvePaymentTypeId(api);

        // Post each cost line
        foreach (var costLine in costLines)
        {
            costLine["travelExpense"] = new { id = travelId };

            // Set payment type if not already set
            if (!costLine.ContainsKey("paymentType") && cachedPaymentTypeId.HasValue)
            {
                costLine["paymentType"] = new { id = cachedPaymentTypeId.Value };
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

    private async Task<long?> ResolveEmployeeByName(TripletexApiClient api, string firstName, string lastName)
    {
        var result = await api.GetAsync("/employee", new Dictionary<string, string>
        {
            ["count"] = "1",
            ["fields"] = "id",
            ["firstName"] = firstName,
            ["lastName"] = lastName
        });
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
                return v.GetProperty("id").GetInt64();
        }
        return null;
    }

    private async Task<long?> ResolveEmployeeByEmail(TripletexApiClient api, string email)
    {
        var result = await api.GetAsync("/employee", new Dictionary<string, string>
        {
            ["count"] = "1",
            ["fields"] = "id",
            ["email"] = email
        });
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

    /// <summary>Get a string value, returning GetRawText() for Object types (use for general fields).</summary>
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

    /// <summary>Get a scalar string value only (String or Number). Returns null for Object/Array types
    /// to avoid accidentally using JSON text as query parameter values.</summary>
    private static string? GetScalarString(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
        {
            if (val is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.String => je.GetString(),
                    JsonValueKind.Number => je.GetRawText(),
                    _ => null // Don't return raw JSON for Objects/Arrays
                };
            }
            var s = val.ToString();
            // Guard: don't return JSON objects as scalar strings
            if (s != null && s.TrimStart().StartsWith('{')) return null;
            return s;
        }
        return null;
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null && !body.ContainsKey(key))
            body[key] = val is JsonElement je ? je.ToString() : val;
    }
}
