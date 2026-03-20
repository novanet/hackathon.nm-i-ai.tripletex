using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class PayrollHandler : ITaskHandler
{
    private readonly ILogger<PayrollHandler> _logger;

    public PayrollHandler(ILogger<PayrollHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
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

        var searchParams = new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" };
        if (!string.IsNullOrEmpty(email))
            searchParams["email"] = email;
        else
        {
            if (!string.IsNullOrEmpty(firstName)) searchParams["firstName"] = firstName;
            if (!string.IsNullOrEmpty(lastName)) searchParams["lastName"] = lastName;
        }

        var empResult = await api.GetAsync("/employee", searchParams);
        if (!empResult.TryGetProperty("values", out var emps) || emps.GetArrayLength() == 0)
        {
            _logger.LogWarning("Employee not found for payroll");
            return;
        }
        var employeeId = emps[0].GetProperty("id").GetInt64();
        _logger.LogInformation("Found employee {Id} for payroll", employeeId);

        // Extract payroll details from "payroll" or "salary" entity
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
            return;
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

        var txResult = await api.PostAsync("/salary/transaction?generateTaxDeduction=true", transactionBody);
        var transactionId = txResult.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created salary transaction {Id} for employee {EmpId}: base={Base}, bonus={Bonus}",
            transactionId, employeeId, baseSalary, bonus);
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
