using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class PayrollHandler : ITaskHandler
{
    private readonly ILogger<PayrollHandler> _logger;

    public PayrollHandler(ILogger<PayrollHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var emp = extracted.Entities.GetValueOrDefault("employee") ?? new();

        // Find the employee by email (or name)
        string? email = GetString(emp, "email");
        string? firstName = GetString(emp, "firstName");
        string? lastName = GetString(emp, "lastName");

        // Handle name splitting if only "name" is provided
        if (string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName) && emp.TryGetValue("name", out var nameObj))
        {
            var fullName = (nameObj is JsonElement nameJe ? nameJe.GetString() : nameObj?.ToString()) ?? "";
            var parts = fullName.Trim().Split(' ', 2);
            firstName = parts[0];
            lastName = parts.Length > 1 ? parts[1] : parts[0];
        }

        // Extract payroll details early (needed for employment date)
        var payroll = extracted.Entities.GetValueOrDefault("payroll") ?? extracted.Entities.GetValueOrDefault("salary") ?? new();
        decimal baseSalary = GetDecimal(payroll, "baseSalary") ?? GetDecimal(payroll, "amount") ?? 0;
        decimal bonus = GetDecimal(payroll, "bonus") ?? 0;

        // Also check raw_amounts as fallback
        if (baseSalary == 0 && extracted.RawAmounts.Count > 0 && decimal.TryParse(extracted.RawAmounts[0], out var rawAmt))
            baseSalary = rawAmt;
        if (bonus == 0 && extracted.RawAmounts.Count > 1 && decimal.TryParse(extracted.RawAmounts[1], out var rawBonus))
            bonus = rawBonus;

        // Determine month/year — use current month if not specified
        int year = DateTime.Now.Year;
        int month = DateTime.Now.Month;
        string voucherDate = $"{year}-{month:D2}-01";

        if (extracted.Dates.Count > 0 && DateTime.TryParse(extracted.Dates[0], out var parsedDate))
        {
            year = parsedDate.Year;
            month = parsedDate.Month;
            voucherDate = extracted.Dates[0];
        }

        // Search for employee
        var searchParams = new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id,dateOfBirth,version" };
        if (!string.IsNullOrEmpty(email))
            searchParams["email"] = email;
        else
        {
            if (!string.IsNullOrEmpty(firstName)) searchParams["firstName"] = firstName;
            if (!string.IsNullOrEmpty(lastName)) searchParams["lastName"] = lastName;
        }

        long employeeId;
        var empResult = await api.GetAsync("/employee", searchParams);
        if (!empResult.TryGetProperty("values", out var emps) || emps.GetArrayLength() == 0)
        {
            // Employee not found — create them
            _logger.LogInformation("Employee not found, creating for payroll: {First} {Last}", firstName, lastName);

            // Need a department (required field)
            var deptResult = await api.GetAsync("/department", new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" });
            long? deptId = null;
            if (deptResult.TryGetProperty("values", out var depts) && depts.GetArrayLength() > 0)
                deptId = depts[0].GetProperty("id").GetInt64();

            var empBody = new Dictionary<string, object>
            {
                ["firstName"] = firstName ?? "Unknown",
                ["lastName"] = lastName ?? "Unknown",
                ["userType"] = "STANDARD",
                ["dateOfBirth"] = "1990-01-01"
            };
            if (!string.IsNullOrEmpty(email)) empBody["email"] = email;
            if (deptId != null) empBody["department"] = new { id = deptId.Value };

            var createResult = await api.PostAsync("/employee", empBody);
            employeeId = createResult.GetProperty("value").GetProperty("id").GetInt64();
            _logger.LogInformation("Created employee {Id} for payroll", employeeId);
        }
        else
        {
            var empEl = emps[0];
            employeeId = empEl.GetProperty("id").GetInt64();
            _logger.LogInformation("Found employee {Id} for payroll", employeeId);

            // Employment requires dateOfBirth — patch if missing
            var hasDob = empEl.TryGetProperty("dateOfBirth", out var dobProp)
                && dobProp.ValueKind != JsonValueKind.Null
                && !string.IsNullOrEmpty(dobProp.GetString());
            if (!hasDob)
            {
                var version = empEl.TryGetProperty("version", out var vProp) ? vProp.GetInt32() : 1;
                _logger.LogInformation("Employee {Id} missing dateOfBirth, patching with default", employeeId);
                await api.PutAsync($"/employee/{employeeId}", new
                {
                    id = employeeId,
                    version,
                    dateOfBirth = "1990-01-01"
                });
            }
        }

        // Ensure employee has an employment for the payroll period
        await EnsureEmployment(api, employeeId, year, month);

        // Ensure SMART_WAGE module is active (required for payslips to be listable)
        try
        {
            await api.PostAsync("/company/salesmodules", new { name = "SMART_WAGE", costStartDate = voucherDate });
            _logger.LogInformation("Activated SMART_WAGE module");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SMART_WAGE activation: {Msg}", ex.Message);
        }

        // Get salary types to find base salary type and bonus type
        var salaryTypesResult = await api.GetAsync("/salary/type", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,number,name"
        });

        long? baseSalaryTypeId = null;
        long? bonusTypeId = null;

        if (salaryTypesResult.TryGetProperty("values", out var salaryTypes))
        {
            foreach (var st in salaryTypes.EnumerateArray())
            {
                var number = st.TryGetProperty("number", out var numProp) ? numProp.GetString() : "";
                var name = st.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";

                // Salary type number "1000" or name containing "Fastlønn"/"Fast lønn" = base salary
                if (number == "1000" || (name != null && (name.Contains("Fastlønn", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Fast lønn", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Månedslønn", StringComparison.OrdinalIgnoreCase))))
                {
                    baseSalaryTypeId = st.GetProperty("id").GetInt64();
                }

                // Salary type number "1350" or name containing "Bonus" = bonus
                if (number == "1350" || (name != null && name.Contains("Bonus", StringComparison.OrdinalIgnoreCase)))
                {
                    bonusTypeId = st.GetProperty("id").GetInt64();
                }
            }

            // Fallback: if no bonus type found, try broader search in 1300-range
            if (bonusTypeId == null && bonus > 0)
            {
                foreach (var st in salaryTypes.EnumerateArray())
                {
                    var number = st.TryGetProperty("number", out var numProp) ? numProp.GetString() : "";
                    if (number != null && number.StartsWith("13"))
                    {
                        bonusTypeId = st.GetProperty("id").GetInt64();
                        break;
                    }
                }
            }
        }

        if (baseSalaryTypeId == null)
        {
            _logger.LogWarning("Could not find base salary type (Fastlønn/1000)");
            return HandlerResult.Empty;
        }

        _logger.LogInformation("Salary types: base={BaseId}, bonus={BonusId}", baseSalaryTypeId, bonusTypeId);

        // Build salary specifications for the payslip
        var specifications = new List<object>();

        // Base salary specification
        specifications.Add(new
        {
            salaryType = new { id = baseSalaryTypeId },
            rate = baseSalary,
            count = 1
        });

        // Bonus specification (if any)
        if (bonus > 0 && bonusTypeId != null)
        {
            specifications.Add(new
            {
                salaryType = new { id = bonusTypeId },
                rate = bonus,
                count = 1
            });
        }

        // Build the full transaction with inline payslip containing specifications
        var transactionBody = new
        {
            date = voucherDate,
            year,
            month,
            payslips = new[]
            {
                new
                {
                    employee = new { id = employeeId },
                    date = voucherDate,
                    year,
                    month,
                    specifications
                }
            }
        };

        _logger.LogInformation("Salary transaction body: {Body}", System.Text.Json.JsonSerializer.Serialize(transactionBody));
        var txResult = await api.PostAsync("/salary/transaction?generateTaxDeduction=true", transactionBody);
        var txValue = txResult.GetProperty("value");
        var transactionId = txValue.GetProperty("id").GetInt64();

        // Log response details for debugging
        var payslipCount = txValue.TryGetProperty("payslips", out var ps) ? ps.GetArrayLength() : -1;
        _logger.LogInformation("Created salary transaction {Id} for employee {EmpId}: base={Base}, bonus={Bonus}, payslips={PayslipCount}",
            transactionId, employeeId, baseSalary, bonus, payslipCount);

        // Verify payslips were created by querying them
        try
        {
            // First check the transaction itself
            var txCheck = await api.GetAsync($"/salary/transaction/{transactionId}", new Dictionary<string, string>
            {
                ["fields"] = "id,payslips,year,month,date"
            });
            var txVal = txCheck.GetProperty("value");
            _logger.LogInformation("Transaction verify: {Data}", txCheck.GetRawText().Substring(0, Math.Min(1000, txCheck.GetRawText().Length)));

            // Try to get the specific payslip by ID
            long firstPayslipId = 0;
            if (txVal.TryGetProperty("payslips", out var payslipArr) && payslipArr.GetArrayLength() > 0)
            {
                firstPayslipId = payslipArr[0].GetProperty("id").GetInt64();
                try
                {
                    var specificPayslip = await api.GetAsync($"/salary/payslip/{firstPayslipId}",
                        new Dictionary<string, string> { ["fields"] = "id,employee,grossAmount,amount,year,month" });
                    _logger.LogInformation("Specific payslip {Id}: {Data}", firstPayslipId, specificPayslip.GetRawText());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Specific payslip {Id} failed: {Msg}", firstPayslipId, ex.Message);
                }
            }

            // Query payslips - try different methods
            var payslips = await api.GetAsync("/salary/payslip", new Dictionary<string, string>
            {
                ["from"] = "0",
                ["count"] = "10",
                ["id"] = firstPayslipId.ToString()
            });
            _logger.LogInformation("Payslip list (by id): {Data}", payslips.GetRawText().Substring(0, Math.Min(500, payslips.GetRawText().Length)));

            // Try salary compilation 
            try
            {
                var comp = await api.GetAsync("/salary/compilation", new Dictionary<string, string>
                {
                    ["employeeId"] = employeeId.ToString(),
                    ["year"] = year.ToString()
                });
                _logger.LogInformation("Salary compilation: {Data}", comp.GetRawText().Substring(0, Math.Min(1000, comp.GetRawText().Length)));
            }
            catch (Exception ex2)
            {
                _logger.LogWarning("Salary compilation failed: {Msg}", ex2.Message);
            }

            // Check salary settings
            try
            {
                var settings = await api.GetAsync("/salary/settings");
                _logger.LogInformation("Salary settings: {Data}", settings.GetRawText().Substring(0, Math.Min(500, settings.GetRawText().Length)));
            }
            catch (Exception ex3)
            {
                _logger.LogWarning("Salary settings failed: {Msg}", ex3.Message);
            }

            // Check active sales modules
            try
            {
                var modules = await api.GetAsync("/company/salesmodules", new Dictionary<string, string>
                {
                    ["from"] = "0",
                    ["count"] = "100"
                });
                _logger.LogInformation("Active sales modules: {Data}", modules.GetRawText().Substring(0, Math.Min(1000, modules.GetRawText().Length)));
            }
            catch (Exception ex4)
            {
                _logger.LogWarning("Sales modules check failed: {Msg}", ex4.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Payslip verification failed: {Msg}", ex.Message);
        }
        return new HandlerResult { EntityType = "salaryTransaction", EntityId = transactionId };
    }

    private async Task EnsureEmployment(TripletexApiClient api, long employeeId, int year, int month)
    {
        // Get or create division (virksomhet) — required for payroll
        long? divisionId = null;
        var divisions = await api.GetAsync("/division", new Dictionary<string, string>
        {
            ["count"] = "1",
            ["fields"] = "id"
        });
        if (divisions.TryGetProperty("values", out var divs) && divs.GetArrayLength() > 0)
        {
            divisionId = divs[0].GetProperty("id").GetInt64();
        }
        else
        {
            // No division exists — create one (required for payroll/A-melding)
            _logger.LogInformation("No division found, creating one for payroll");

            // Get a municipality reference
            var munis = await api.GetAsync("/municipality", new Dictionary<string, string>
            {
                ["count"] = "1",
                ["fields"] = "id"
            });
            long? muniId = null;
            if (munis.TryGetProperty("values", out var muniValues) && muniValues.GetArrayLength() > 0)
                muniId = muniValues[0].GetProperty("id").GetInt64();

            // Get the company's org number
            string orgNumber = "999999999";
            var companyDivs = await api.GetAsync("/company/divisions", new Dictionary<string, string>
            {
                ["count"] = "1",
                ["fields"] = "id,organizationNumber,name"
            });
            if (companyDivs.TryGetProperty("values", out var compDivs) && compDivs.GetArrayLength() > 0)
            {
                var orgProp = compDivs[0].TryGetProperty("organizationNumber", out var on) ? on.GetString() : null;
                if (!string.IsNullOrEmpty(orgProp)) orgNumber = orgProp;
            }

            var today = $"{year}-{month:D2}-01";
            var divBody = new Dictionary<string, object>
            {
                ["name"] = "Hovedvirksomhet",
                ["organizationNumber"] = orgNumber,
                ["startDate"] = today,
                ["municipalityDate"] = today
            };
            if (muniId != null)
                divBody["municipality"] = new { id = muniId.Value };

            var divResult = await api.PostAsync("/division", divBody);
            divisionId = divResult.GetProperty("value").GetProperty("id").GetInt64();
            _logger.LogInformation("Created division {Id}", divisionId);
        }

        // Check if employee has an employment
        var employments = await api.GetAsync("/employee/employment", new Dictionary<string, string>
        {
            ["employeeId"] = employeeId.ToString(),
            ["count"] = "1",
            ["fields"] = "id,version,division"
        });

        if (employments.TryGetProperty("values", out var empValues) && empValues.GetArrayLength() > 0)
        {
            // Update existing employment with division
            var existing = empValues[0];
            var empId = existing.GetProperty("id").GetInt64();
            var version = existing.GetProperty("version").GetInt32();
            _logger.LogInformation("Updating employment {Id} with division {Div}", empId, divisionId);
            await api.PutAsync($"/employee/employment/{empId}", new
            {
                id = empId,
                version,
                employee = new { id = employeeId },
                division = new { id = divisionId!.Value },
                taxDeductionCode = "loennFraHovedarbeidsgiver"
            });
            return;
        }

        // Create employment starting at the beginning of the payroll month
        var startDate = $"{year}-{month:D2}-01";
        _logger.LogInformation("Creating employment for employee {Id} starting {Date} division {Div}", employeeId, startDate, divisionId);

        await api.PostAsync("/employee/employment", new
        {
            employee = new { id = employeeId },
            startDate,
            division = new { id = divisionId!.Value },
            taxDeductionCode = "loennFraHovedarbeidsgiver",
            employmentDetails = new[]
            {
                new
                {
                    date = startDate,
                    employmentType = (string?)"ORDINARY",
                    employmentForm = (string?)"PERMANENT",
                    remunerationType = (string?)"MONTHLY_WAGE",
                    workingHoursScheme = (string?)"NOT_SHIFT",
                    percentageOfFullTimeEquivalent = 100.0m
                }
            }
        });
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
            if (je.ValueKind == JsonValueKind.String && decimal.TryParse(je.GetString(), out var d)) return d;
        }
        if (val is decimal dv) return dv;
        if (decimal.TryParse(val?.ToString(), out var d2)) return d2;
        return null;
    }
}
