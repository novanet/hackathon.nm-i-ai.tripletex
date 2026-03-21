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

        // Generate synthetic email when missing or empty (e.g. PDF offer letters rarely include one).
        // STANDARD userType requires email — without it we get a 422.
        var emailMissing = !body.ContainsKey("email")
            || string.IsNullOrWhiteSpace(body["email"]?.ToString());
        if (emailMissing)
        {
            var fn = (body.TryGetValue("firstName", out var fnv) ? fnv?.ToString() : null) ?? "employee";
            var ln = (body.TryGetValue("lastName", out var lnv) ? lnv?.ToString() : null) ?? "noreply";
            var syntheticEmail = BuildSyntheticEmail(fn, ln);
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

        // Extract startDate for employment (separate API object)
        string? startDate = null;
        if (emp.TryGetValue("startDate", out var sdObj))
            startDate = (sdObj is JsonElement sdJe ? sdJe.GetString() : sdObj?.ToString());

        // Determine if admin role is needed
        var hasRoles = emp.TryGetValue("roles", out var rolesObj);
        var roles = ParseStringList(rolesObj);
        var needsAdmin = roles.Any(r => r.Equals("administrator", StringComparison.OrdinalIgnoreCase)
            || r.Equals("admin", StringComparison.OrdinalIgnoreCase));

        body["userType"] = needsAdmin ? "EXTENDED" : "STANDARD";

        // Handle department — REQUIRED when department module is active (competition environments always have it).
        // Always fetch the first available department as a fallback. If a specific department name is
        // in Relationships, search for it; otherwise just take the first one.
        long? deptId = null;
        extracted.Relationships.TryGetValue("department", out var deptName);
        if (string.IsNullOrWhiteSpace(deptName)
            && extracted.Entities.TryGetValue("department", out var deptEntity)
            && deptEntity.TryGetValue("name", out var deptEntityName))
        {
            deptName = deptEntityName is JsonElement deptJe ? deptJe.ToString() : deptEntityName?.ToString();
        }
        var deptResult = await api.GetAsync("/department", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,name"
        });
        if (deptResult.TryGetProperty("values", out var depts) && depts.GetArrayLength() > 0)
        {
            if (!string.IsNullOrWhiteSpace(deptName))
            {
                foreach (var department in depts.EnumerateArray())
                {
                    var existingDeptName = department.TryGetProperty("name", out var deptNameProp) ? deptNameProp.GetString() : null;
                    if (string.Equals(existingDeptName, deptName, StringComparison.OrdinalIgnoreCase))
                    {
                        deptId = department.GetProperty("id").GetInt64();
                        break;
                    }
                }
            }

            deptId ??= depts[0].GetProperty("id").GetInt64();
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
        // Pre-fetch division for inline employment — sandbox requires it, GET is free
        long? divisionId = null;
        if (!string.IsNullOrEmpty(startDate))
        {
            divisionId = await ResolveDivisionIdAsync(api);
            var inlineEmployment = new Dictionary<string, object> { ["startDate"] = startDate };
            if (divisionId != null)
                inlineEmployment["division"] = new Dictionary<string, object> { ["id"] = divisionId.Value };
            body["employments"] = new[] { inlineEmployment };
        }

        _logger.LogInformation("Creating employee: {FirstName} {LastName} (inlineEmployment={HasEmployment})",
            body.GetValueOrDefault("firstName"), body.GetValueOrDefault("lastName"), !string.IsNullOrEmpty(startDate));

        // Pre-check email to avoid 422 errors from duplicate email — GET is free
        var existingByEmail = await TryFindExistingEmployeeByEmailAsync(api, body);
        long employeeId;
        if (existingByEmail != null)
        {
            employeeId = existingByEmail.Value.GetProperty("id").GetInt64();
            _logger.LogInformation("Employee with matching email+name already exists (ID {Id}), reusing", employeeId);
            await EnsureEmployeeMatchesBodyAsync(api, employeeId, body);
        }
        else
        {
            var apiResult = await CreateEmployeeWithRetryAsync(api, body);
            var reusedExistingEmployee = !apiResult.TryGetProperty("value", out var valueWrapper);
            var employeeValue = reusedExistingEmployee ? apiResult : valueWrapper;
            employeeId = employeeValue.GetProperty("id").GetInt64();

            if (reusedExistingEmployee)
                await EnsureEmployeeMatchesBodyAsync(api, employeeId, body);
        }

        _logger.LogInformation("Created employee ID: {Id}", employeeId);

        var result = new HandlerResult { EntityType = "employee", EntityId = employeeId };

        await TryCreateEmploymentDetailsAsync(api, emp, employeeId, startDate, divisionId);

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

    private async Task<JsonElement> CreateEmployeeWithRetryAsync(TripletexApiClient api, Dictionary<string, object> body)
    {
        try
        {
            return await api.PostAsync("/employee", body);
        }
        catch (TripletexApiException ex) when (ex.StatusCode == 422 && body.ContainsKey("employments"))
        {
            _logger.LogInformation("Inline employment failed (422), retrying without employment");
            body.Remove("employments");
        }

        try
        {
            return await api.PostAsync("/employee", body);
        }
        catch (TripletexApiException ex) when (ex.StatusCode == 422 && IsDuplicateEmailError(ex))
        {
            var existingEmployee = await TryFindExistingEmployeeByEmailAsync(api, body);
            if (existingEmployee != null)
            {
                _logger.LogInformation("Duplicate email belongs to existing matching employee, reusing employee ID {EmployeeId}", existingEmployee.Value.GetProperty("id").GetInt64());
                return existingEmployee.Value;
            }

            var firstName = (body.TryGetValue("firstName", out var fnv) ? fnv?.ToString() : null) ?? "employee";
            var lastName = (body.TryGetValue("lastName", out var lnv) ? lnv?.ToString() : null) ?? "noreply";
            var uniqueEmail = BuildUniqueEmail(firstName, lastName, body["email"]?.ToString());
            body["email"] = uniqueEmail;
            _logger.LogInformation("Duplicate employee email rejected by API, retrying with unique email: {Email}", uniqueEmail);
            return await api.PostAsync("/employee", body);
        }
    }

    private async Task<JsonElement?> TryFindExistingEmployeeByEmailAsync(TripletexApiClient api, Dictionary<string, object> body)
    {
        var email = body.TryGetValue("email", out var emailObj) ? emailObj?.ToString() : null;
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var existing = await api.GetAsync("/employee", new Dictionary<string, string>
        {
            ["email"] = email,
            ["count"] = "10",
            ["fields"] = "id,firstName,lastName,email"
        });

        if (!existing.TryGetProperty("values", out var values))
            return null;

        var expectedFirstName = body.TryGetValue("firstName", out var firstNameObj) ? firstNameObj?.ToString() : null;
        var expectedLastName = body.TryGetValue("lastName", out var lastNameObj) ? lastNameObj?.ToString() : null;

        foreach (var candidate in values.EnumerateArray())
        {
            var candidateFirstName = candidate.TryGetProperty("firstName", out var fnProp) ? fnProp.GetString() : null;
            var candidateLastName = candidate.TryGetProperty("lastName", out var lnProp) ? lnProp.GetString() : null;

            if (string.Equals(candidateFirstName, expectedFirstName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidateLastName, expectedLastName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task TryCreateEmploymentDetailsAsync(TripletexApiClient api, Dictionary<string, object> emp, long employeeId, string? startDate, long? divisionId = null)
    {
        if (string.IsNullOrWhiteSpace(startDate))
            return;

        var hasEmploymentDetailData = HasValue(emp, "jobCode")
            || HasValue(emp, "occupationCode")
            || HasValue(emp, "employmentPercentage")
            || HasValue(emp, "percentageOfFullTimeEquivalent")
            || HasValue(emp, "annualSalary")
            || HasValue(emp, "workingHoursPerDay")
            || HasValue(emp, "dailyWorkingHours")
            || HasValue(emp, "standardWorkHoursPerDay")
            || HasValue(emp, "employmentType")
            || HasValue(emp, "employmentForm")
            || HasValue(emp, "salaryType")
            || HasValue(emp, "remunerationType");

        if (!hasEmploymentDetailData)
            return;

        divisionId ??= await ResolveDivisionIdAsync(api);
        var employmentId = await EnsureEmploymentAsync(api, employeeId, startDate, divisionId);
        if (employmentId == null)
            return;

        var detailsBody = new Dictionary<string, object>
        {
            ["employment"] = new Dictionary<string, object> { ["id"] = employmentId.Value },
            ["date"] = startDate,
        };

        var occupationCode = GetString(emp, "occupationCode") ?? GetString(emp, "jobCode");
        var occupationName = GetString(emp, "occupationName");
        if (!string.IsNullOrWhiteSpace(occupationCode))
        {
            var occupationCodeId = await ResolveOccupationCodeIdAsync(api, occupationCode, occupationName);
            if (occupationCodeId != null)
                detailsBody["occupationCode"] = new Dictionary<string, object> { ["id"] = occupationCodeId.Value };
        }

        var percentage = GetDecimal(emp, "percentageOfFullTimeEquivalent") ?? GetDecimal(emp, "employmentPercentage");
        if (percentage != null)
            detailsBody["percentageOfFullTimeEquivalent"] = percentage.Value;

        var annualSalary = GetDecimal(emp, "annualSalary");
        if (annualSalary != null)
            detailsBody["annualSalary"] = annualSalary.Value;

        var remunerationType = MapRemunerationType(GetString(emp, "remunerationType") ?? GetString(emp, "salaryType"));
        if (!string.IsNullOrWhiteSpace(remunerationType))
            detailsBody["remunerationType"] = remunerationType;

        var employmentType = MapEmploymentType(GetString(emp, "employmentType"));
        if (!string.IsNullOrWhiteSpace(employmentType))
            detailsBody["employmentType"] = employmentType;

        var employmentForm = MapEmploymentForm(GetString(emp, "employmentForm") ?? GetString(emp, "employmentType"));
        if (!string.IsNullOrWhiteSpace(employmentForm))
            detailsBody["employmentForm"] = employmentForm;

        var workingHours = GetDecimal(emp, "workingHoursPerDay")
            ?? GetDecimal(emp, "dailyWorkingHours")
            ?? GetDecimal(emp, "standardWorkHoursPerDay");
        if (workingHours != null)
        {
            detailsBody["workingHoursScheme"] = "NOT_SHIFT";
            detailsBody["shiftDurationHours"] = workingHours.Value;
        }

        var existingEmploymentDetails = await GetEmploymentDetailsForDateAsync(api, employmentId.Value, startDate);
        if (existingEmploymentDetails != null)
        {
            await UpdateEmploymentDetailsAsync(api, employmentId.Value, existingEmploymentDetails.Value, detailsBody, employeeId, startDate);
            return;
        }

        await api.PostAsync("/employee/employment/details", detailsBody);
        _logger.LogInformation("Created employment details for employee {EmployeeId}", employeeId);
    }

    private async Task<long?> EnsureEmploymentAsync(TripletexApiClient api, long employeeId, string startDate, long? divisionId)
    {
        var employmentResult = await api.GetAsync("/employee/employment", new Dictionary<string, string>
        {
            ["employeeId"] = employeeId.ToString(),
            ["count"] = "1",
            ["fields"] = "id,version,startDate,division(id)"
        });

        if (employmentResult.TryGetProperty("values", out var employments) && employments.GetArrayLength() > 0)
        {
            var employment = employments[0];
            var employmentId = employment.GetProperty("id").GetInt64();
            var hasDivision = employment.TryGetProperty("division", out var division)
                && division.ValueKind == JsonValueKind.Object
                && division.TryGetProperty("id", out _);

            var existingStartDate = employment.TryGetProperty("startDate", out var sdProp) ? sdProp.GetString() : null;
            var needsDivisionUpdate = !hasDivision && divisionId != null;
            var needsStartDateUpdate = existingStartDate != null
                && string.Compare(startDate, existingStartDate, StringComparison.Ordinal) < 0;

            if ((needsDivisionUpdate || needsStartDateUpdate) && employment.TryGetProperty("version", out var versionProp))
            {
                var updateBody = new Dictionary<string, object>
                {
                    ["id"] = employmentId,
                    ["version"] = versionProp.GetInt64(),
                    ["employee"] = new Dictionary<string, object> { ["id"] = employeeId },
                    ["startDate"] = needsStartDateUpdate ? startDate : existingStartDate!,
                };
                if (divisionId != null)
                    updateBody["division"] = new Dictionary<string, object> { ["id"] = divisionId.Value };
                else if (hasDivision)
                    updateBody["division"] = division;

                await api.PutAsync($"/employee/employment/{employmentId}", updateBody);
            }

            return employmentId;
        }

        try
        {
            var body = new Dictionary<string, object>
            {
                ["employee"] = new Dictionary<string, object> { ["id"] = employeeId },
                ["startDate"] = startDate,
            };

            if (divisionId != null)
                body["division"] = new Dictionary<string, object> { ["id"] = divisionId.Value };

            var created = await api.PostAsync("/employee/employment", body);
            return created.GetProperty("value").GetProperty("id").GetInt64();
        }
        catch (TripletexApiException ex)
        {
            _logger.LogWarning(ex, "Failed to ensure employment for employee {EmployeeId}", employeeId);
            return null;
        }
    }

    // Common STYRK-08 (4-digit) → Norwegian occupation name mapping for nameNO search fallback
    private static readonly Dictionary<string, string> Styrk08ToName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1120"] = "direktør",
        ["1211"] = "økonomisjef",
        ["1221"] = "salgssjef",
        ["1330"] = "IT-sjef",
        ["2120"] = "matematiker",
        ["2141"] = "industriingeniør",
        ["2151"] = "elektroingeniør",
        ["2166"] = "grafisk designer",
        ["2211"] = "lege",
        ["2223"] = "sykepleier",
        ["2310"] = "universitetslektor",
        ["2330"] = "lektor",
        ["2411"] = "revisor",
        ["2431"] = "markedsfører",
        ["2511"] = "systemutvikler",
        ["2512"] = "programvareutvikler",
        ["2519"] = "utvikler",
        ["2521"] = "databaseadministrator",
        ["2522"] = "systemadministrator",
        ["3111"] = "tekniker",
        ["3313"] = "regnskapsfører",
        ["3323"] = "regnskapsfører",
        ["3331"] = "speditør",
        ["3411"] = "jurist",
        ["4110"] = "kontormedarbeider",
        ["4120"] = "sekretær",
        ["4131"] = "regnskapsmedarbeider",
        ["5223"] = "butikkmedarbeider",
        ["7115"] = "tømrer",
        ["7126"] = "rørlegger",
        ["7231"] = "bilmekaniker",
        ["7411"] = "elektriker",
        ["8332"] = "sjåfør",
        ["9112"] = "renholdsarbeider",
    };

    private async Task<long?> ResolveOccupationCodeIdAsync(TripletexApiClient api, string occupationCode, string? occupationName = null)
    {
        try
        {
            // 1. Try exact/containing code filter match (single GET)
            long? match = await SearchOccupationCodesPageAsync(api, occupationCode, includeCodeFilter: true);
            if (match != null)
                return match;

            // 2. Fallback: search by Norwegian occupation name (handles STYRK-08 → STYRK-98 mapping)
            // Derive name from: LLM extraction > static dictionary
            var searchName = occupationName;
            if (string.IsNullOrWhiteSpace(searchName) && Styrk08ToName.TryGetValue(occupationCode, out var dictName))
                searchName = dictName;

            if (!string.IsNullOrWhiteSpace(searchName))
            {
                _logger.LogInformation("Code {Code} not found, trying nameNO search with '{Name}'", occupationCode, searchName);
                var nameResult = await api.GetAsync("/employee/employment/occupationCode", new Dictionary<string, string>
                {
                    ["nameNO"] = searchName,
                    ["count"] = "10",
                    ["fields"] = "id,code,nameNO"
                });
                if (nameResult.TryGetProperty("values", out var nameValues) && nameValues.GetArrayLength() > 0)
                {
                    // Prefer exact name match, then first result
                    foreach (var v in nameValues.EnumerateArray())
                    {
                        var name = v.TryGetProperty("nameNO", out var nameProp) ? nameProp.GetString() : null;
                        if (name != null && name.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Found name match: {Code} {Name}", v.GetProperty("code").GetString(), name);
                            return v.GetProperty("id").GetInt64();
                        }
                    }
                    // No containing match — take first result
                    var first = nameValues[0];
                    _logger.LogInformation("Using first nameNO match: {Code} {Name}",
                        first.GetProperty("code").GetString(),
                        first.TryGetProperty("nameNO", out var fn) ? fn.GetString() : "?");
                    return first.GetProperty("id").GetInt64();
                }
            }
        }
        catch (TripletexApiException ex)
        {
            _logger.LogWarning(ex, "Failed to resolve occupation code {OccupationCode}", occupationCode);
        }

        return null;
    }

    private async Task<long?> SearchOccupationCodesPageAsync(TripletexApiClient api, string occupationCode, bool includeCodeFilter)
    {
        const int pageSize = 1000;
        var from = 0;
        long? prefixMatch = null;

        while (true)
        {
            var query = new Dictionary<string, string>
            {
                ["from"] = from.ToString(),
                ["count"] = pageSize.ToString(),
                ["fields"] = "id,code,nameNO"
            };

            if (includeCodeFilter)
                query["code"] = occupationCode;

            var result = await api.GetAsync("/employee/employment/occupationCode", query);
            if (!result.TryGetProperty("values", out var values) || values.GetArrayLength() == 0)
                return prefixMatch; // Return prefix match if found during scan

            foreach (var value in values.EnumerateArray())
            {
                var code = value.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
                if (string.Equals(code, occupationCode, StringComparison.OrdinalIgnoreCase))
                    return value.GetProperty("id").GetInt64();
                // Prefix match: 4-digit STYRK-08 code matches start of 7-digit code
                if (prefixMatch == null && code != null && code.StartsWith(occupationCode, StringComparison.OrdinalIgnoreCase))
                    prefixMatch = value.GetProperty("id").GetInt64();
            }

            var fullResultSize = result.TryGetProperty("fullResultSize", out var sizeProp) ? sizeProp.GetInt32() : values.GetArrayLength();
            from += values.GetArrayLength();
            if (from >= fullResultSize)
                return prefixMatch; // Return prefix match if exact match not found
        }
    }

    private async Task EnsureEmployeeMatchesBodyAsync(TripletexApiClient api, long employeeId, Dictionary<string, object> desiredBody)
    {
        var existing = await api.GetAsync($"/employee/{employeeId}", new Dictionary<string, string>
        {
            ["fields"] = "id,version,firstName,lastName,dateOfBirth,userType,email,department(id)"
        });

        if (!existing.TryGetProperty("value", out var value))
            return;

        var updateBody = new Dictionary<string, object>
        {
            ["id"] = value.GetProperty("id").GetInt64(),
            ["version"] = value.GetProperty("version").GetInt64(),
        };

        foreach (var field in new[] { "firstName", "lastName", "dateOfBirth", "userType", "email", "department" })
        {
            if (desiredBody.TryGetValue(field, out var desiredValue))
            {
                updateBody[field] = desiredValue;
            }
            else if (value.TryGetProperty(field, out var existingValue) && existingValue.ValueKind != JsonValueKind.Null)
            {
                updateBody[field] = existingValue;
            }
        }

        await api.PutAsync($"/employee/{employeeId}", updateBody);
    }

    private async Task<JsonElement?> GetEmploymentDetailsForDateAsync(TripletexApiClient api, long employmentId, string date)
    {
        try
        {
            var result = await api.GetAsync($"/employee/employment/{employmentId}", new Dictionary<string, string>
            {
                ["fields"] = "id,employmentDetails(id,version,date,occupationCode(id,code),percentageOfFullTimeEquivalent,annualSalary,employmentType,employmentForm,remunerationType)"
            });

            if (!result.TryGetProperty("value", out var value)
                || !value.TryGetProperty("employmentDetails", out var details)
                || details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var detail in details.EnumerateArray())
            {
                var existingDate = detail.TryGetProperty("date", out var dateProp) ? dateProp.GetString() : null;
                if (string.Equals(existingDate, date, StringComparison.OrdinalIgnoreCase))
                    return detail;
            }
        }
        catch (TripletexApiException ex)
        {
            _logger.LogWarning(ex, "Failed to inspect existing employment details for employment {EmploymentId}", employmentId);
        }

        return null;
    }

    private async Task UpdateEmploymentDetailsAsync(TripletexApiClient api, long employmentId, JsonElement existingDetails, Dictionary<string, object> desiredBody, long employeeId, string startDate)
    {
        var updateBody = new Dictionary<string, object>(desiredBody)
        {
            ["id"] = existingDetails.GetProperty("id").GetInt64(),
            ["version"] = existingDetails.GetProperty("version").GetInt64(),
        };

        await api.PutAsync($"/employee/employment/details/{updateBody["id"]}", updateBody);
        _logger.LogInformation("Updated employment details for employee {EmployeeId} on {Date}", employeeId, startDate);
    }

    private async Task<long?> ResolveDivisionIdAsync(TripletexApiClient api)
    {
        try
        {
            var result = await api.GetAsync("/division", new Dictionary<string, string>
            {
                ["count"] = "1",
                ["fields"] = "id"
            });

            if (result.TryGetProperty("values", out var values) && values.GetArrayLength() > 0)
                return values[0].GetProperty("id").GetInt64();
        }
        catch (TripletexApiException ex)
        {
            _logger.LogWarning(ex, "Failed to resolve division for employment details");
        }

        return null;
    }

    private static bool HasValue(Dictionary<string, object> entity, string key)
        => !string.IsNullOrWhiteSpace(GetString(entity, key));

    private static string? GetString(Dictionary<string, object> entity, string key)
    {
        if (!entity.TryGetValue(key, out var value) || value is null)
            return null;

        return value is JsonElement je ? je.ToString() : value.ToString();
    }

    private static decimal? GetDecimal(Dictionary<string, object> entity, string key)
    {
        var value = GetString(entity, key);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.GetCultureInfo("nb-NO"), out parsed))
            return parsed;

        return null;
    }

    private static string? MapEmploymentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lower = value.Trim().ToLowerInvariant();
        if (lower.Contains("freelance")) return "FREELANCE";
        if (lower.Contains("maritime")) return "MARITIME";
        return "ORDINARY";
    }

    private static string? MapEmploymentForm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lower = value.Trim().ToLowerInvariant();
        if (lower.Contains("temporary") || lower.Contains("midlertid") || lower.Contains("tempor")) return "TEMPORARY";
        if (lower.Contains("call")) return "TEMPORARY_ON_CALL";
        return "PERMANENT";
    }

    private static string? MapRemunerationType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lower = value.Trim().ToLowerInvariant();
        if (lower.Contains("hour") || lower.Contains("time") || lower.Contains("timel")) return "HOURLY_WAGE";
        if (lower.Contains("commission") || lower.Contains("provis")) return "COMMISION_PERCENTAGE";
        if (lower.Contains("fee") || lower.Contains("honorar")) return "FEE";
        return "MONTHLY_WAGE";
    }

    private static bool IsDuplicateEmailError(TripletexApiException ex)
        => ex.Message.Contains("e-postadressen", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("email", StringComparison.OrdinalIgnoreCase);

    private static string BuildSyntheticEmail(string firstName, string lastName)
    {
        var fn = SanitizeEmailPart(firstName, "employee");
        var ln = SanitizeEmailPart(lastName, "noreply");
        return $"{fn}.{ln}@example.org";
    }

    private static string BuildUniqueEmail(string firstName, string lastName, string? currentEmail)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        if (!string.IsNullOrWhiteSpace(currentEmail) && currentEmail.Contains('@'))
        {
            var parts = currentEmail.Split('@', 2);
            var local = SanitizeEmailPart(parts[0], "employee");
            var domain = SanitizeEmailPart(parts[1], "example.org").Replace(".", "-");
            return $"{local}.{stamp}@{parts[1]}";
        }

        var fn = SanitizeEmailPart(firstName, "employee");
        var ln = SanitizeEmailPart(lastName, "noreply");
        return $"{fn}.{ln}.{stamp}@example.org";
    }

    private static string SanitizeEmailPart(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var cleaned = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '.')
            .ToArray())
            .Trim('.');

        while (cleaned.Contains("..", StringComparison.Ordinal))
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
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
