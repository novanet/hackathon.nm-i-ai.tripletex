using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class ProjectHandler : ITaskHandler
{
    private readonly ILogger<ProjectHandler> _logger;

    public ProjectHandler(ILogger<ProjectHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var project = extracted.Entities.GetValueOrDefault("project") ?? new();

        var body = new Dictionary<string, object>();
        var handlerResult = new HandlerResult { EntityType = "project" };

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

        // Default startDate to today if still missing (API requires it)
        if (!body.ContainsKey("startDate"))
            body["startDate"] = DateTime.Today.ToString("yyyy-MM-dd");

        // Set fixed price
        decimal? fixedPriceAmount = null;
        if (project.TryGetValue("fixedPrice", out var fpVal) || project.TryGetValue("fixedprice", out fpVal))
        {
            if (fpVal is JsonElement fpElem && fpElem.ValueKind == JsonValueKind.Number)
                fixedPriceAmount = fpElem.GetDecimal();
            else if (decimal.TryParse(fpVal?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var fpParsed))
                fixedPriceAmount = fpParsed;
        }
        if (fixedPriceAmount.HasValue)
        {
            body["fixedprice"] = fixedPriceAmount.Value;
            body["isFixedPrice"] = true;
        }

        // Set boolean flags
        if (project.TryGetValue("isInternal", out var isInt))
            body["isInternal"] = bool.Parse(isInt.ToString()!);
        if (project.TryGetValue("isFixedPrice", out var isFp) && !body.ContainsKey("isFixedPrice"))
            body["isFixedPrice"] = bool.Parse(isFp.ToString()!);

        // Resolve customer if referenced
        var customerName = extracted.Relationships.GetValueOrDefault("customer")
            ?? GetStringField(project, "customer")
            ?? GetStringField(project, "customerName");
        var orgNumber = GetStringField(project, "organizationNumber")
            ?? GetStringField(project, "orgNumber")
            ?? GetStringField(project, "customerOrgNumber");
        // If customerName looks like an org number (all digits), treat it as one
        if (!string.IsNullOrEmpty(customerName) && System.Text.RegularExpressions.Regex.IsMatch(customerName, @"^\d{9,}$"))
        {
            orgNumber ??= customerName;
            customerName = null;
        }
        if (customerName != null || orgNumber != null)
        {
            var customerId = await ResolveCustomerId(api, customerName, orgNumber);
            if (customerId.HasValue)
                body["customer"] = new Dictionary<string, object> { ["id"] = customerId.Value };
        }

        // Resolve project manager (required field)
        // The LLM may return projectManager as a nested object inside project, or as a separate entity
        string? managerFirstName = null, managerLastName = null, managerEmail = null;

        // Check for projectManager as a separate entity first
        var pmEntity = extracted.Entities.GetValueOrDefault("projectManager");
        if (pmEntity != null && pmEntity.Count > 0)
        {
            managerFirstName = GetScalarString(pmEntity, "firstName");
            managerLastName = GetScalarString(pmEntity, "lastName");
            managerEmail = GetScalarString(pmEntity, "email");
        }

        // Then check inside project entity
        if (managerFirstName == null && (project.TryGetValue("projectManager", out var pmVal) || project.TryGetValue("manager", out pmVal)))
        {
            if (pmVal is JsonElement pmElem && pmElem.ValueKind == JsonValueKind.Object)
            {
                managerFirstName = pmElem.TryGetProperty("firstName", out var fn) ? fn.GetString() : null;
                managerLastName = pmElem.TryGetProperty("lastName", out var ln) ? ln.GetString() : null;
                managerEmail = pmElem.TryGetProperty("email", out var em) ? em.GetString() : null;
            }
            else
            {
                var nameStr = pmVal?.ToString();
                if (!string.IsNullOrEmpty(nameStr))
                {
                    // Guard: if ToString() returns JSON (e.g. {"firstName":...}), parse it
                    var trimmed = nameStr.Trim();
                    if (trimmed.StartsWith('{'))
                    {
                        try
                        {
                            var doc = JsonDocument.Parse(trimmed);
                            var root = doc.RootElement;
                            managerFirstName = root.TryGetProperty("firstName", out var fn2) ? fn2.GetString() : null;
                            managerLastName = root.TryGetProperty("lastName", out var ln2) ? ln2.GetString() : null;
                            managerEmail = root.TryGetProperty("email", out var em2) ? em2.GetString() : null;
                        }
                        catch { /* fall through to split */ }
                    }
                    if (managerFirstName == null)
                    {
                        var parts = nameStr.Split(' ', 2);
                        managerFirstName = parts[0];
                        managerLastName = parts.Length > 1 ? parts[1] : null;
                    }
                }
            }
        }
        else
        {
            var nameFromRel = extracted.Relationships.GetValueOrDefault("projectManager");
            if (!string.IsNullOrEmpty(nameFromRel))
            {
                var parts = nameFromRel.Split(' ', 2);
                managerFirstName = parts[0];
                managerLastName = parts.Length > 1 ? parts[1] : null;
            }
        }

        // Try to find the manager employee, or create them
        if (managerFirstName != null)
        {
            var managerId = await ResolveEmployeeByFields(api, managerFirstName, managerLastName, managerEmail);
            if (!managerId.HasValue)
            {
                // Create the employee so we can assign them as manager
                var empBody = new Dictionary<string, object> { ["firstName"] = managerFirstName };
                if (managerLastName != null) empBody["lastName"] = managerLastName;
                if (managerEmail != null) empBody["email"] = managerEmail;
                empBody["userType"] = "EXTENDED";
                // Need a department
                var deptResult = await api.GetAsync("/department", new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" });
                if (deptResult.TryGetProperty("values", out var dv) && dv.GetArrayLength() > 0)
                    empBody["department"] = new Dictionary<string, object> { ["id"] = dv[0].GetProperty("id").GetInt64() };
                empBody["dateOfBirth"] = "1990-01-01";
                var empResult = await api.PostAsync("/employee", empBody);
                managerId = empResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created employee {First} {Last} (ID: {Id}) as project manager", managerFirstName, managerLastName, managerId);
            }
            // Always grant entitlements so they can be project manager
            try { await api.PutAsync($"/employee/entitlement/:grantEntitlementsByTemplate?employeeId={managerId}&template=ALL_PRIVILEGES", null); }
            catch { /* may already have entitlements */ }
            body["projectManager"] = new Dictionary<string, object> { ["id"] = managerId.Value };
        }
        // If no manager specified, use the first employee as fallback
        if (!body.ContainsKey("projectManager"))
        {
            var fallbackId = await ResolveFirstEmployeeId(api);
            if (fallbackId.HasValue)
                body["projectManager"] = new Dictionary<string, object> { ["id"] = fallbackId.Value };
        }

        // Resolve department
        var deptName = extracted.Relationships.GetValueOrDefault("department")
            ?? GetStringField(project, "department");
        if (deptName != null)
        {
            var deptId = await ResolveDepartmentId(api, deptName);
            if (deptId.HasValue)
                body["department"] = new Dictionary<string, object> { ["id"] = deptId.Value };
        }

        _logger.LogInformation("Creating project: {Name}, isFixedPrice={IsFixed}, fixedprice={FixedAmt}",
            body.GetValueOrDefault("name"), body.GetValueOrDefault("isFixedPrice"), body.GetValueOrDefault("fixedprice"));

        var result = await api.PostAsync("/project", body);
        var projectId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created project ID: {Id}", projectId);
        handlerResult.EntityId = projectId;

        // Handle timesheet entries (log hours + generate project invoice)
        var timesheet = extracted.Entities.GetValueOrDefault("timesheet");
        if (timesheet != null && timesheet.Count > 0)
        {
            await HandleTimesheetAndInvoice(api, extracted, projectId, body, timesheet);
            return handlerResult;
        }

        // For fixed-price projects with a percentage in the prompt, ALWAYS create the milestone invoice
        // (don't gate on LLM extracting an "invoice" entity — that's unreliable)
        bool isFixedPrice = body.ContainsKey("isFixedPrice") && body["isFixedPrice"] is true;
        bool hasPercentage = extracted.RawPrompt != null && System.Text.RegularExpressions.Regex.IsMatch(extracted.RawPrompt, @"\d+\s*%");
        var invoiceEntity = extracted.Entities.GetValueOrDefault("invoice");
        if (isFixedPrice && hasPercentage)
        {
            await CreateProjectInvoice(api, extracted, projectId, body);
        }
        else if (invoiceEntity != null && invoiceEntity.Count > 0)
        {
            await CreateProjectInvoice(api, extracted, projectId, body);
        }
        return handlerResult;
    }

    private async Task HandleTimesheetAndInvoice(TripletexApiClient api, ExtractionResult extracted,
        long projectId, Dictionary<string, object> projectBody, Dictionary<string, object> timesheet)
    {
        // 1. Enable SMART_TIME_TRACKING module (ignore if already active)
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

        // 2. Extract timesheet data
        decimal hours = GetDecimalField(timesheet, "hours");
        decimal hourlyRate = GetDecimalField(timesheet, "hourlyRate");
        var activityName = GetStringField(timesheet, "activityName") ?? "Rådgivning";

        // 3. Resolve employee (from employee entity or project manager)
        long? employeeId = null;
        var empEntity = extracted.Entities.GetValueOrDefault("employee");
        if (empEntity != null)
        {
            var fn = GetScalarString(empEntity, "firstName");
            var ln = GetScalarString(empEntity, "lastName");
            var em = GetScalarString(empEntity, "email");
            if (fn != null)
                employeeId = await ResolveEmployeeByFields(api, fn, ln, em);

            // Create employee if not found
            if (!employeeId.HasValue && fn != null)
            {
                var empBody = new Dictionary<string, object> { ["firstName"] = fn };
                if (ln != null) empBody["lastName"] = ln;
                if (em != null) empBody["email"] = em;
                empBody["userType"] = "EXTENDED";
                empBody["dateOfBirth"] = "1990-01-01";
                var deptResult = await api.GetAsync("/department", new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" });
                if (deptResult.TryGetProperty("values", out var dv) && dv.GetArrayLength() > 0)
                    empBody["department"] = new Dictionary<string, object> { ["id"] = dv[0].GetProperty("id").GetInt64() };
                var empResult = await api.PostAsync("/employee", empBody);
                employeeId = empResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created employee {First} {Last} (ID: {Id})", fn, ln, employeeId);
            }
        }
        // Fallback: use project manager
        if (!employeeId.HasValue && projectBody.TryGetValue("projectManager", out var pmObj))
        {
            var pmJson = JsonSerializer.Serialize(pmObj);
            using var pmDoc = JsonDocument.Parse(pmJson);
            if (pmDoc.RootElement.TryGetProperty("id", out var pmId))
                employeeId = pmId.GetInt64();
        }
        if (!employeeId.HasValue)
            employeeId = await ResolveFirstEmployeeId(api);

        // 4. Resolve or create activity, then link to project via projectActivity
        long activityId = 0;

        // Always search for existing activity first to avoid wasted error calls
        var existingActSearch = await api.GetAsync("/activity",
            new Dictionary<string, string> { ["name"] = activityName, ["count"] = "1", ["fields"] = "id" });
        long existingActId = 0;
        if (existingActSearch.TryGetProperty("values", out var existingActVals) && existingActVals.GetArrayLength() > 0)
            existingActId = existingActVals[0].GetProperty("id").GetInt64();

        if (existingActId > 0)
        {
            // Activity exists — link it to the project
            _logger.LogInformation("Activity '{Name}' already exists (id={Id}), linking to project", activityName, existingActId);
            try
            {
                var paLinkBody = new Dictionary<string, object>
                {
                    ["project"] = new Dictionary<string, object> { ["id"] = projectId },
                    ["activity"] = new Dictionary<string, object> { ["id"] = existingActId }
                };
                if (hourlyRate > 0) paLinkBody["budgetHourlyRateCurrency"] = hourlyRate;
                await api.PostAsync("/project/projectActivity", paLinkBody);
                _logger.LogInformation("Linked existing activity {Id} to project {ProjId}", existingActId, projectId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to link existing activity {Id} to project {ProjId} (may already be linked)", existingActId, projectId);
            }
            activityId = existingActId;
        }
        else
        {
            // Activity doesn't exist — create it inline with the project link
            try
            {
                var paBody = new Dictionary<string, object>
                {
                    ["project"] = new Dictionary<string, object> { ["id"] = projectId },
                    ["activity"] = new Dictionary<string, object>
                    {
                        ["name"] = activityName,
                        ["activityType"] = "PROJECT_GENERAL_ACTIVITY"
                    }
                };
                if (hourlyRate > 0) paBody["budgetHourlyRateCurrency"] = hourlyRate;
                var paResult = await api.PostAsync("/project/projectActivity", paBody);
                var paValue = paResult.GetProperty("value");
                if (paValue.TryGetProperty("activity", out var actProp) && actProp.TryGetProperty("id", out var actIdProp))
                    activityId = actIdProp.GetInt64();
                _logger.LogInformation("Created projectActivity with activity '{Name}' (activityId: {Id})", activityName, activityId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create projectActivity inline");
                // Should be very rare since we searched first, but handle gracefully
                var retrySearch = await api.GetAsync("/activity",
                    new Dictionary<string, string> { ["name"] = activityName, ["count"] = "1", ["fields"] = "id" });
                if (retrySearch.TryGetProperty("values", out var actVals) && actVals.GetArrayLength() > 0)
                    activityId = actVals[0].GetProperty("id").GetInt64();
            }
        }

        // 6. Create timesheet entry
        if (hours > 0 && activityId > 0 && employeeId.HasValue)
        {
            try
            {
                await api.PostAsync("/timesheet/entry", new
                {
                    project = new Dictionary<string, object> { ["id"] = projectId },
                    activity = new Dictionary<string, object> { ["id"] = activityId },
                    employee = new Dictionary<string, object> { ["id"] = employeeId.Value },
                    date = DateTime.Today.ToString("yyyy-MM-dd"),
                    hours
                });
                _logger.LogInformation("Logged {Hours} hours for employee {EmpId} on project {ProjId}", hours, employeeId.Value, projectId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create timesheet entry");
            }
        }

        // 7. Create invoice based on hours × hourly rate
        decimal invoiceAmount = hours * hourlyRate;
        if (invoiceAmount > 0)
        {
            long? customerId = null;
            if (projectBody.TryGetValue("customer", out var custObj))
            {
                var json = JsonSerializer.Serialize(custObj);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                    customerId = idProp.GetInt64();
            }
            if (customerId.HasValue)
            {
                await EnsureBankAccount(api);

                var invoiceDate = DateTime.Today.ToString("yyyy-MM-dd");
                var orderResult = await api.PostAsync("/order", new
                {
                    customer = new Dictionary<string, object> { ["id"] = customerId.Value },
                    project = new Dictionary<string, object> { ["id"] = projectId },
                    orderDate = invoiceDate,
                    deliveryDate = invoiceDate,
                    orderLines = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["description"] = $"{activityName} - {hours}t × {hourlyRate} NOK/t",
                            ["count"] = hours,
                            ["unitPriceExcludingVatCurrency"] = hourlyRate
                        }
                    }
                });
                var orderId = orderResult.GetProperty("value").GetProperty("id").GetInt64();

                var invoiceResult = await api.PostAsync("/invoice", new
                {
                    invoiceDate,
                    invoiceDueDate = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"),
                    orders = new[] { new Dictionary<string, object> { ["id"] = orderId } }
                });
                var invoiceId = invoiceResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created project invoice ID: {Id}, amount: {Amount} NOK excl. VAT", invoiceId, invoiceAmount);
            }
        }
    }

    private static decimal GetDecimalField(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Number)
                return je.GetDecimal();
            if (decimal.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return 0m;
    }

    private async Task CreateProjectInvoice(TripletexApiClient api, ExtractionResult extracted, long projectId, Dictionary<string, object> projectBody)
    {
        var invoiceEntity = extracted.Entities.GetValueOrDefault("invoice") ?? new();
        var project = extracted.Entities.GetValueOrDefault("project") ?? new();

        // 1. Get fixed price from project body
        decimal fixedPrice = 0m;
        if (projectBody.TryGetValue("fixedprice", out var fpObj))
        {
            if (fpObj is decimal d) fixedPrice = d;
            else decimal.TryParse(fpObj.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out fixedPrice);
        }

        // 2. Extract percentage from prompt using regex (e.g. "33%", "50 %", "25 %")
        decimal percentage = 0m;
        if (extracted.RawPrompt != null)
        {
            var pctMatch = System.Text.RegularExpressions.Regex.Match(extracted.RawPrompt, @"(\d+)\s*%");
            if (pctMatch.Success)
                decimal.TryParse(pctMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out percentage);
        }

        // 3. Calculate milestone amount deterministically if we have both fixedPrice and percentage
        decimal invoiceAmount = 0m;
        if (fixedPrice > 0 && percentage > 0)
        {
            invoiceAmount = Math.Round(fixedPrice * percentage / 100m, 2);
            _logger.LogInformation("Calculated milestone amount: {FixedPrice} × {Pct}% = {Amount}", fixedPrice, percentage, invoiceAmount);
        }

        // 4. Fall back to LLM-extracted amount from invoice entity
        if (invoiceAmount <= 0m && invoiceEntity.TryGetValue("amount", out var amtVal))
        {
            if (amtVal is JsonElement amtElem && amtElem.ValueKind == JsonValueKind.Number)
                invoiceAmount = amtElem.GetDecimal();
            else
                decimal.TryParse(amtVal?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out invoiceAmount);
        }

        // 5. Fall back to orderLines[0].unitPrice from invoice entity
        if (invoiceAmount <= 0m && invoiceEntity.TryGetValue("orderLines", out var olVal))
        {
            if (olVal is JsonElement olElem && olElem.ValueKind == JsonValueKind.Array && olElem.GetArrayLength() > 0)
            {
                var firstLine = olElem[0];
                if (firstLine.TryGetProperty("unitPrice", out var up) && up.ValueKind == JsonValueKind.Number)
                    invoiceAmount = up.GetDecimal();
            }
        }

        // 6. Last resort: RawAmounts
        if (invoiceAmount <= 0m && extracted.RawAmounts.Count >= 2)
        {
            decimal.TryParse(extracted.RawAmounts[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out invoiceAmount);
        }

        if (invoiceAmount <= 0m)
        {
            _logger.LogWarning("No invoice amount found for project invoice, skipping");
            return;
        }

        // Resolve customer ID from project body
        long? customerId = null;
        if (projectBody.TryGetValue("customer", out var custObj))
        {
            var json = JsonSerializer.Serialize(custObj);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
                customerId = idProp.GetInt64();
        }
        if (!customerId.HasValue)
        {
            _logger.LogWarning("No customer ID for project invoice, skipping");
            return;
        }

        // Ensure bank account
        await EnsureBankAccount(api);

        // Create order with a single line for the invoice amount, linked to project (no vatType — project-linked orders inherit vatType from project)
        var invoiceDate = DateTime.Now.ToString("yyyy-MM-dd");
        var orderBody = new Dictionary<string, object>
        {
            ["customer"] = new Dictionary<string, object> { ["id"] = customerId.Value },
            ["project"] = new Dictionary<string, object> { ["id"] = projectId },
            ["orderDate"] = invoiceDate,
            ["deliveryDate"] = invoiceDate,
            ["orderLines"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["description"] = GetStringField(invoiceEntity, "description") ?? $"Delfakturering prosjekt",
                    ["count"] = 1,
                    ["unitPriceExcludingVatCurrency"] = invoiceAmount
                }
            }
        };

        var orderResult = await api.PostAsync("/order", orderBody);
        var orderId = orderResult.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created order ID: {Id} for project invoice", orderId);

        // Create invoice
        var invoiceDueDate = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");
        var invoiceBody = new Dictionary<string, object>
        {
            ["invoiceDate"] = invoiceDate,
            ["invoiceDueDate"] = invoiceDueDate,
            ["orders"] = new[] { new Dictionary<string, object> { ["id"] = orderId } }
        };

        var invoiceResult = await api.PostAsync("/invoice", invoiceBody);
        var invoiceId = invoiceResult.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created project invoice ID: {Id}, amount: {Amount}", invoiceId, invoiceAmount);

        // NOTE: preliminaryInvoice field on PUT /project is silently ignored (always reads back as null)
        // Probing confirmed this — skip the GET+PUT to save 2 API calls
    }

    private async Task EnsureBankAccount(TripletexApiClient api)
    {
        try
        {
            var result = await api.GetAsync("/ledger/account", new Dictionary<string, string>
            {
                ["number"] = "1920",
                ["count"] = "1",
                ["fields"] = "id,version,bankAccountNumber,isBankAccount"
            });
            if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            {
                var acct = vals[0];
                var bankNum = acct.TryGetProperty("bankAccountNumber", out var bn) ? bn.GetString() : null;
                if (string.IsNullOrEmpty(bankNum))
                {
                    var id = acct.GetProperty("id").GetInt64();
                    var version = acct.GetProperty("version").GetInt32();
                    await api.PutAsync($"/ledger/account/{id}", new { id, version, bankAccountNumber = "86011117947", isBankAccount = true });
                    _logger.LogInformation("Set bank account number on ledger account 1920");
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureBankAccount failed (non-fatal)"); }
    }

    private async Task<long> ResolveVatTypeId(TripletexApiClient api)
    {
        var result = await api.GetAsync("/ledger/vatType", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,number,percentage"
        });
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var vt in vals.EnumerateArray())
            {
                if (vt.TryGetProperty("number", out var num))
                {
                    var n = num.ValueKind == JsonValueKind.Number ? num.GetInt32()
                        : int.TryParse(num.GetString(), out var parsed) ? parsed : -1;
                    if (n == 3) return vt.GetProperty("id").GetInt64();
                }
            }
            // Fallback to first with percentage > 0
            foreach (var vt in vals.EnumerateArray())
            {
                if (vt.TryGetProperty("percentage", out var pct) && pct.GetDecimal() > 0)
                    return vt.GetProperty("id").GetInt64();
            }
            if (vals.GetArrayLength() > 0)
                return vals[0].GetProperty("id").GetInt64();
        }
        return 0;
    }

    private async Task<long?> ResolveCustomerId(TripletexApiClient api, string? name, string? orgNumber)
    {
        // Try by organization number first (more precise)
        if (!string.IsNullOrEmpty(orgNumber))
        {
            var result = await api.GetAsync("/customer", new Dictionary<string, string>
            {
                ["organizationNumber"] = orgNumber,
                ["count"] = "1",
                ["fields"] = "id,name"
            });
            if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
                return vals[0].GetProperty("id").GetInt64();
        }
        // Then try by name
        if (!string.IsNullOrEmpty(name))
        {
            var result = await api.GetAsync("/customer", new Dictionary<string, string>
            {
                ["name"] = name,
                ["count"] = "1",
                ["fields"] = "id,name"
            });
            if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
                return vals[0].GetProperty("id").GetInt64();
        }
        // Customer not found — create it
        var custBody = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(name)) custBody["name"] = name;
        else if (!string.IsNullOrEmpty(orgNumber)) custBody["name"] = orgNumber;
        else return null;
        if (!string.IsNullOrEmpty(orgNumber)) custBody["organizationNumber"] = orgNumber;
        var createResult = await api.PostAsync("/customer", custBody);
        return createResult.GetProperty("value").GetProperty("id").GetInt64();
    }

    private async Task<long?> ResolveEmployeeByFields(TripletexApiClient api, string firstName, string? lastName, string? email)
    {
        var query = new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" };
        query["firstName"] = firstName;
        if (!string.IsNullOrEmpty(lastName))
            query["lastName"] = lastName;
        var result = await api.GetAsync("/employee", query);
        if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            return vals[0].GetProperty("id").GetInt64();

        // Try by email if name search failed
        if (!string.IsNullOrEmpty(email))
        {
            var query2 = new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id", ["email"] = email };
            var result2 = await api.GetAsync("/employee", query2);
            if (result2.TryGetProperty("values", out var vals2) && vals2.GetArrayLength() > 0)
                return vals2[0].GetProperty("id").GetInt64();
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

    private async Task<long?> ResolveFirstEmployeeId(TripletexApiClient api)
    {
        var result = await api.GetAsync("/employee", new Dictionary<string, string>
        {
            ["count"] = "1",
            ["fields"] = "id"
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
                return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
            return val.ToString();
        }
        return null;
    }

    private static string? GetScalarString(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
        {
            if (val is JsonElement je)
                return je.ValueKind == JsonValueKind.String ? je.GetString() : null;
            var s = val.ToString();
            return s != null && !s.TrimStart().StartsWith('{') ? s : null;
        }
        return null;
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
            body[key] = val is JsonElement je ? je.ToString() : val;
    }
}
