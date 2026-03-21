using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles standalone create_timesheet tasks (hour logging without project invoicing).
/// API chain:
///   1. Enable SMART_TIME_TRACKING module (idempotent)
///   2. Resolve employee (by name/email, create if missing)
///   3. Optionally resolve project (by name)
///   4. Resolve/create activity + link to project
///   5. POST /timesheet/entry
/// </summary>
public class TimesheetHandler : ITaskHandler
{
    private readonly ILogger<TimesheetHandler> _logger;

    public TimesheetHandler(ILogger<TimesheetHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var timesheet = extracted.Entities.GetValueOrDefault("timesheet") ?? new();

        // ── Step 1: Enable SMART_TIME_TRACKING ──────────────────────────────────
        try
        {
            var modules = await api.GetAsync("/company/salesmodules",
                new Dictionary<string, string> { ["name"] = "SMART_TIME_TRACKING", ["count"] = "1", ["fields"] = "id" });
            if (!modules.TryGetProperty("values", out var mv) || mv.GetArrayLength() == 0)
            {
                await api.PostAsync("/company/salesmodules",
                    new { name = "SMART_TIME_TRACKING", costStartDate = DateTime.Today.ToString("yyyy-MM-dd") });
                _logger.LogInformation("Enabled SMART_TIME_TRACKING module");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMART_TIME_TRACKING check/activation failed");
        }

        // ── Step 2: Resolve employee ─────────────────────────────────────────────
        long? employeeId = null;

        // Primary: employee entity
        var empEntity = extracted.Entities.GetValueOrDefault("employee") ?? new();
        var empFirst = GetScalarString(empEntity, "firstName");
        var empLast = GetScalarString(empEntity, "lastName");
        var empEmail = GetScalarString(empEntity, "email");

        // Fallback: regex on raw prompt
        if (empFirst == null && empLast == null && extracted.RawPrompt != null)
        {
            var m = Regex.Match(extracted.RawPrompt,
                @"(?:for|pour|para|für|f[oö]r)\s+([A-ZÆØÅÄÖÜ][a-zæøåäöüéèêëàáâãíìîïóòôõúùûü]+)\s+([A-ZÆØÅÄÖÜ][a-zæøåäöüéèêëàáâãíìîïóòôõúùûü]+)",
                RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (m.Success) { empFirst = m.Groups[1].Value; empLast = m.Groups[2].Value; }
        }
        if (empEmail == null && extracted.RawPrompt != null)
        {
            var em2 = Regex.Match(extracted.RawPrompt, @"[\w.+-]+@[\w.-]+\.\w+",
                RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (em2.Success) empEmail = em2.Value;
        }

        if (empFirst != null && empLast != null)
            employeeId = await ResolveEmployeeByName(api, empFirst, empLast);
        if (!employeeId.HasValue && empEmail != null)
            employeeId = await ResolveEmployeeByEmail(api, empEmail);

        // Create employee if still not found
        if (!employeeId.HasValue && empFirst != null)
        {
            _logger.LogInformation("Employee not found, creating: {First} {Last}", empFirst, empLast);
            var empBody = new Dictionary<string, object>
            {
                ["firstName"] = empFirst,
                ["userType"] = "EXTENDED",
                ["dateOfBirth"] = "1990-01-01"
            };
            if (empLast != null) empBody["lastName"] = empLast;
            if (empEmail != null) empBody["email"] = empEmail;
            var deptRes = await api.GetAsync("/department", new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" });
            if (deptRes.TryGetProperty("values", out var dv) && dv.GetArrayLength() > 0)
                empBody["department"] = new Dictionary<string, object> { ["id"] = dv[0].GetProperty("id").GetInt64() };
            var empResult = await api.PostAsync("/employee", empBody);
            employeeId = empResult.GetProperty("value").GetProperty("id").GetInt64();
        }

        if (!employeeId.HasValue)
        {
            // Last resort: first employee in system
            var anyEmp = await api.GetAsync("/employee", new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" });
            if (anyEmp.TryGetProperty("values", out var empVals) && empVals.GetArrayLength() > 0)
                employeeId = empVals[0].GetProperty("id").GetInt64();
        }

        // ── Step 3: Extract timesheet fields ─────────────────────────────────────
        decimal hours = GetDecimalField(timesheet, "hours");
        if (hours <= 0 && extracted.RawAmounts.Count > 0)
            decimal.TryParse(extracted.RawAmounts[0].Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out hours);
        if (hours <= 0) hours = 1m; // sensible default

        var activityName = GetStringField(timesheet, "activityName")
            ?? GetStringField(timesheet, "activity")
            ?? "Rådgivning";

        var date = GetStringField(timesheet, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : null)
            ?? DateTime.Today.ToString("yyyy-MM-dd");

        // ── Step 4: Optionally resolve project ───────────────────────────────────
        long? projectId = null;
        var projEntity = extracted.Entities.GetValueOrDefault("project");
        if (projEntity != null && projEntity.Count > 0)
        {
            var projName = GetStringField(projEntity, "name");
            if (projName != null)
            {
                var projSearch = await api.GetAsync("/project",
                    new Dictionary<string, string> { ["name"] = projName, ["count"] = "1", ["fields"] = "id" });
                if (projSearch.TryGetProperty("values", out var pv) && pv.GetArrayLength() > 0)
                    projectId = pv[0].GetProperty("id").GetInt64();
            }
        }

        // ── Step 5: Resolve / create activity + link to project ──────────────────
        long activityId = 0;

        // Search for existing activity
        var actSearch = await api.GetAsync("/activity",
            new Dictionary<string, string> { ["name"] = activityName, ["count"] = "1", ["fields"] = "id" });
        long existingActId = 0;
        if (actSearch.TryGetProperty("values", out var actVals) && actVals.GetArrayLength() > 0)
            existingActId = actVals[0].GetProperty("id").GetInt64();

        if (projectId.HasValue)
        {
            if (existingActId > 0)
            {
                // Link existing activity to project
                try
                {
                    await api.PostAsync("/project/projectActivity", new
                    {
                        project = new Dictionary<string, object> { ["id"] = projectId.Value },
                        activity = new Dictionary<string, object> { ["id"] = existingActId }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not link activity {Id} to project (may already be linked)", existingActId);
                }
                activityId = existingActId;
            }
            else
            {
                // Create activity inline with project link
                try
                {
                    var paResult = await api.PostAsync("/project/projectActivity", new
                    {
                        project = new Dictionary<string, object> { ["id"] = projectId.Value },
                        activity = new Dictionary<string, object>
                        {
                            ["name"] = activityName,
                            ["activityType"] = "PROJECT_GENERAL_ACTIVITY"
                        }
                    });
                    var paValue = paResult.GetProperty("value");
                    if (paValue.TryGetProperty("activity", out var actProp) && actProp.TryGetProperty("id", out var actIdProp))
                        activityId = actIdProp.GetInt64();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create projectActivity inline");
                    // Re-search after failure
                    var retry = await api.GetAsync("/activity",
                        new Dictionary<string, string> { ["name"] = activityName, ["count"] = "1", ["fields"] = "id" });
                    if (retry.TryGetProperty("values", out var rv) && rv.GetArrayLength() > 0)
                        activityId = rv[0].GetProperty("id").GetInt64();
                }
            }
        }
        else
        {
            // No project — use existing activity or create a general one
            if (existingActId > 0)
            {
                activityId = existingActId;
            }
            else
            {
                try
                {
                    var actResult = await api.PostAsync("/activity", new
                    {
                        name = activityName,
                        activityType = "GENERAL_ACTIVITY",
                        isProjectActivity = false
                    });
                    activityId = actResult.GetProperty("value").GetProperty("id").GetInt64();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create standalone activity '{Name}'", activityName);
                }
            }
        }

        if (activityId == 0)
        {
            _logger.LogWarning("Could not resolve activity '{Name}' — timesheet entry will be skipped", activityName);
            return HandlerResult.Empty;
        }

        // ── Step 6: POST timesheet entry ─────────────────────────────────────────
        var entryBody = new Dictionary<string, object>
        {
            ["activity"] = new Dictionary<string, object> { ["id"] = activityId },
            ["date"] = date,
            ["hours"] = hours
        };
        if (employeeId.HasValue)
            entryBody["employee"] = new Dictionary<string, object> { ["id"] = employeeId.Value };
        if (projectId.HasValue)
            entryBody["project"] = new Dictionary<string, object> { ["id"] = projectId.Value };

        var entryResult = await api.PostAsync("/timesheet/entry", entryBody);
        var entryId = entryResult.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created timesheet entry ID: {Id} — {Hours}h of '{Activity}' on {Date}",
            entryId, hours, activityName, date);

        return new HandlerResult { EntityType = "timesheetEntry", EntityId = entryId };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
        return val?.ToString();
    }

    private static string? GetScalarString(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
        if (val is string s) return s;
        return null;
    }

    private static decimal GetDecimalField(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetDecimal();
            if (decimal.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return 0m;
    }

    private async Task<long?> ResolveEmployeeByName(TripletexApiClient api, string firstName, string lastName)
    {
        var res = await api.GetAsync("/employee",
            new Dictionary<string, string> { ["firstName"] = firstName, ["lastName"] = lastName, ["count"] = "1", ["fields"] = "id" });
        if (res.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            return vals[0].GetProperty("id").GetInt64();
        return null;
    }

    private async Task<long?> ResolveEmployeeByEmail(TripletexApiClient api, string email)
    {
        var res = await api.GetAsync("/employee",
            new Dictionary<string, string> { ["email"] = email, ["count"] = "1", ["fields"] = "id" });
        if (res.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            return vals[0].GetProperty("id").GetInt64();
        return null;
    }
}
