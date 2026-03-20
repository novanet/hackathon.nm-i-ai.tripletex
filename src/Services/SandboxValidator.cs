using System.Text.Json;
using TripletexAgent.Models;

namespace TripletexAgent.Services;

public class SandboxValidator
{
    private readonly ILogger<SandboxValidator> _logger;

    public SandboxValidator(ILogger<SandboxValidator> logger)
    {
        _logger = logger;
    }

    public async Task<ValidationReport> ValidateAsync(
        TripletexApiClient api,
        ExtractionResult extracted,
        HandlerResult handlerResult)
    {
        var report = new ValidationReport
        {
            TaskType = extracted.TaskType,
            EntityType = handlerResult.EntityType,
            EntityId = handlerResult.EntityId
        };

        try
        {
            switch (extracted.TaskType)
            {
                case "create_employee":
                case "update_employee":
                    await ValidateEmployee(api, extracted, handlerResult, report);
                    break;
                case "create_customer":
                    await ValidateCustomer(api, extracted, handlerResult, report);
                    break;
                case "create_product":
                    await ValidateProduct(api, extracted, handlerResult, report);
                    break;
                case "create_department":
                    await ValidateDepartment(api, extracted, handlerResult, report);
                    break;
                case "create_supplier":
                    await ValidateSupplier(api, extracted, handlerResult, report);
                    break;
                case "create_invoice":
                    await ValidateInvoice(api, extracted, handlerResult, report);
                    break;
                case "register_payment":
                    await ValidatePayment(api, extracted, handlerResult, report);
                    break;
                case "create_project":
                    await ValidateProject(api, extracted, handlerResult, report);
                    break;
                case "create_travel_expense":
                    await ValidateTravelExpense(api, extracted, handlerResult, report);
                    break;
                case "create_credit_note":
                    await ValidateCreditNote(api, extracted, handlerResult, report);
                    break;
                case "create_voucher":
                    await ValidateVoucher(api, extracted, handlerResult, report);
                    break;
                case "delete_entity":
                    ValidateDelete(handlerResult, report);
                    break;
                case "run_payroll":
                    await ValidatePayroll(api, extracted, handlerResult, report);
                    break;
                default:
                    report.Checks.Add(new ValidationCheck("handler_executed", "true",
                        handlerResult.EntityId.HasValue ? "true" : "unknown", handlerResult.EntityId.HasValue));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Validation failed for {TaskType}", extracted.TaskType);
            report.Error = ex.Message;
        }

        report.CalculateScore();
        _logger.LogInformation("Validation: {TaskType} score={Score}/{MaxScore} ({Pct}%)",
            extracted.TaskType, report.PointsEarned, report.MaxPoints,
            report.MaxPoints > 0 ? (int)(report.PointsEarned * 100.0 / report.MaxPoints) : 0);

        foreach (var check in report.Checks.Where(c => !c.Passed))
        {
            _logger.LogWarning("  FAIL: {Field} expected={Expected} actual={Actual}",
                check.Field, check.Expected, check.Actual);
        }

        return report;
    }

    private async Task ValidateEmployee(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var emp = await api.GetAsync($"/employee/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = emp.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("employee") ?? new();

        // Employee found (2 points)
        report.Checks.Add(new ValidationCheck("employee_found", "true", "true", true, 2));

        // First name (1 point)
        CheckStringField(val, entity, "firstName", report, 1);

        // Last name (1 point)
        CheckStringField(val, entity, "lastName", report, 1);

        // Email (1 point)
        CheckStringField(val, entity, "email", report, 1);

        // Date of birth (optional)
        if (entity.ContainsKey("dateOfBirth"))
            CheckStringField(val, entity, "dateOfBirth", report, 1);

        // Phone
        if (entity.ContainsKey("phoneNumberMobile"))
            CheckStringField(val, entity, "phoneNumberMobile", report, 1);

        // Administrator role (5 points)
        if (result.Metadata.ContainsKey("adminRole"))
        {
            // Verify by checking entitlements
            try
            {
                var entitlements = await api.GetAsync("/employee/entitlement",
                    new Dictionary<string, string>
                    {
                        ["employeeId"] = result.EntityId.Value.ToString(),
                        ["count"] = "100",
                        ["fields"] = "id"
                    });
                var hasEntitlements = entitlements.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0;
                report.Checks.Add(new ValidationCheck("administrator_role", "true",
                    hasEntitlements ? "true" : "false", hasEntitlements, 5));
            }
            catch
            {
                report.Checks.Add(new ValidationCheck("administrator_role", "true", "unknown", false, 5));
            }
        }
    }

    private async Task ValidateCustomer(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var cust = await api.GetAsync($"/customer/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = cust.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("customer")
            ?? extracted.Entities.GetValueOrDefault("customer1") ?? new();

        report.Checks.Add(new ValidationCheck("customer_found", "true", "true", true, 2));
        CheckStringField(val, entity, "name", report, 2);
        CheckStringField(val, entity, "email", report, 1);
        CheckStringField(val, entity, "organizationNumber", report, 1);
        CheckStringField(val, entity, "phoneNumber", report, 1);

        // Address check — competition validates individual address fields, not just presence
        // Extract expected address values from entity (may be stored as address/addressLine1/street + postalCode + city)
        var addrKeys = new[] { "address", "addressLine1", "street", "postalCode", "city" };
        if (addrKeys.Any(k => entity.ContainsKey(k)))
        {
            string? expectedLine1 = entity.TryGetValue("addressLine1", out var l1) ? l1?.ToString()
                : entity.TryGetValue("address", out var l1b) ? l1b?.ToString()
                : entity.TryGetValue("street", out var l1c) ? l1c?.ToString() : null;
            string? expectedPostalCode = entity.TryGetValue("postalCode", out var pc) ? pc?.ToString() : null;
            string? expectedCity = entity.TryGetValue("city", out var ct) ? ct?.ToString() : null;

            // Competition checks physicalAddress fields
            JsonElement addr = default;
            bool hasAddr = val.TryGetProperty("physicalAddress", out addr) && addr.ValueKind == JsonValueKind.Object;

            if (expectedLine1 != null)
            {
                var actual = hasAddr && addr.TryGetProperty("addressLine1", out var prop) ? prop.GetString() : null;
                report.Checks.Add(new ValidationCheck("address.addressLine1", expectedLine1,
                    actual ?? "(null)", string.Equals(expectedLine1.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase), 1));
            }
            if (expectedPostalCode != null)
            {
                var actual = hasAddr && addr.TryGetProperty("postalCode", out var prop) ? prop.GetString() : null;
                report.Checks.Add(new ValidationCheck("address.postalCode", expectedPostalCode,
                    actual ?? "(null)", string.Equals(expectedPostalCode.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase), 1));
            }
            if (expectedCity != null)
            {
                var actual = hasAddr && addr.TryGetProperty("city", out var prop) ? prop.GetString() : null;
                report.Checks.Add(new ValidationCheck("address.city", expectedCity,
                    actual ?? "(null)", string.Equals(expectedCity.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase), 1));
            }
        }
    }

    private async Task ValidateProduct(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var prod = await api.GetAsync($"/product/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = prod.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("product") ?? new();

        report.Checks.Add(new ValidationCheck("product_found", "true", "true", true, 2));
        CheckStringField(val, entity, "name", report, 2);
        CheckStringField(val, entity, "number", report, 1);

        if (entity.ContainsKey("priceExcludingVatCurrency"))
            CheckDecimalField(val, entity, "priceExcludingVatCurrency", report, 1);

        if (entity.ContainsKey("priceIncludingVatCurrency"))
            CheckDecimalField(val, entity, "priceIncludingVatCurrency", report, 1);
    }

    private async Task ValidateDepartment(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var dept = await api.GetAsync($"/department/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = dept.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("department") ?? new();

        report.Checks.Add(new ValidationCheck("department_found", "true", "true", true, 2));
        CheckStringField(val, entity, "name", report, 2);
        CheckStringField(val, entity, "departmentNumber", report, 1);
    }

    private async Task ValidateSupplier(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var sup = await api.GetAsync($"/supplier/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = sup.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("supplier")
            ?? extracted.Entities.GetValueOrDefault("supplier1") ?? new();

        report.Checks.Add(new ValidationCheck("supplier_found", "true", "true", true, 2));
        CheckStringField(val, entity, "name", report, 2);
        CheckStringField(val, entity, "email", report, 1);
        CheckStringField(val, entity, "organizationNumber", report, 1);
        CheckStringField(val, entity, "phoneNumber", report, 1);
    }

    private async Task ValidateInvoice(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var inv = await api.GetAsync($"/invoice/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = inv.GetProperty("value");

        report.Checks.Add(new ValidationCheck("invoice_found", "true", "true", true, 2));

        // Check that invoice has a customer
        if (val.TryGetProperty("customer", out var custRef) && custRef.ValueKind == JsonValueKind.Object)
            report.Checks.Add(new ValidationCheck("has_customer", "true", "true", true, 1));
        else
            report.Checks.Add(new ValidationCheck("has_customer", "true", "false", false, 1));

        // Check amounts
        if (val.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number)
        {
            var amountVal = amount.GetDecimal();
            report.Checks.Add(new ValidationCheck("has_amount", "> 0", amountVal.ToString(), amountVal > 0, 2));
        }
    }

    private async Task ValidatePayment(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var inv = await api.GetAsync($"/invoice/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = inv.GetProperty("value");

        report.Checks.Add(new ValidationCheck("invoice_found", "true", "true", true, 2));

        // Check payment status — reversal expects amountOutstanding > 0, normal payment expects 0
        if (val.TryGetProperty("amountOutstanding", out var outstanding))
        {
            var outstandingVal = outstanding.GetDecimal();
            if (extracted.Action == "reverse")
            {
                report.Checks.Add(new ValidationCheck("payment_reversed", "> 0",
                    outstandingVal.ToString(), outstandingVal > 0, 3));
            }
            else
            {
                report.Checks.Add(new ValidationCheck("payment_registered", "0",
                    outstandingVal.ToString(), outstandingVal == 0, 3));
            }
        }
    }

    private async Task ValidateProject(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var proj = await api.GetAsync($"/project/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = proj.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("project") ?? new();

        report.Checks.Add(new ValidationCheck("project_found", "true", "true", true, 2));
        CheckStringField(val, entity, "name", report, 2);

        if (val.TryGetProperty("customer", out var custRef) && custRef.ValueKind == JsonValueKind.Object
            && custRef.TryGetProperty("id", out var custId) && custId.GetInt64() > 0)
            report.Checks.Add(new ValidationCheck("has_customer", "true", "true", true, 1));

        if (val.TryGetProperty("projectManager", out var pmRef) && pmRef.ValueKind == JsonValueKind.Object
            && pmRef.TryGetProperty("id", out var pmId) && pmId.GetInt64() > 0)
            report.Checks.Add(new ValidationCheck("has_project_manager", "true", "true", true, 1));
    }

    private async Task ValidateTravelExpense(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var te = await api.GetAsync($"/travelExpense/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = te.GetProperty("value");

        report.Checks.Add(new ValidationCheck("travel_expense_found", "true", "true", true, 2));

        if (val.TryGetProperty("title", out var title) && !string.IsNullOrEmpty(title.GetString()))
            report.Checks.Add(new ValidationCheck("has_title", "true", "true", true, 1));

        if (val.TryGetProperty("employee", out var empRef) && empRef.ValueKind == JsonValueKind.Object)
            report.Checks.Add(new ValidationCheck("has_employee", "true", "true", true, 1));

        // Check if costs were added
        try
        {
            var costs = await api.GetAsync("/travelExpense/cost",
                new Dictionary<string, string>
                {
                    ["travelExpenseId"] = result.EntityId.Value.ToString(),
                    ["count"] = "100",
                    ["fields"] = "id"
                });
            if (costs.TryGetProperty("values", out var costVals))
            {
                var costCount = costVals.GetArrayLength();
                report.Checks.Add(new ValidationCheck("has_costs", "> 0",
                    costCount.ToString(), costCount > 0, 2));
            }
        }
        catch { /* cost check is best-effort */ }
    }

    private async Task ValidateCreditNote(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        report.Checks.Add(new ValidationCheck("credit_note_created", "true", "true", true, 3));
    }

    private async Task ValidateVoucher(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var voucher = await api.GetAsync($"/ledger/voucher/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = voucher.GetProperty("value");

        report.Checks.Add(new ValidationCheck("voucher_found", "true", "true", true, 2));

        if (val.TryGetProperty("description", out var desc) && !string.IsNullOrEmpty(desc.GetString()))
            report.Checks.Add(new ValidationCheck("has_description", "true", "true", true, 1));

        // Check postings exist
        if (val.TryGetProperty("postings", out var postings) && postings.ValueKind == JsonValueKind.Array)
        {
            var count = postings.GetArrayLength();
            report.Checks.Add(new ValidationCheck("has_postings", ">= 2",
                count.ToString(), count >= 2, 2));
        }
    }

    private void ValidateDelete(HandlerResult result, ValidationReport report)
    {
        var wasDeleted = result.Metadata.ContainsKey("action") && result.Metadata["action"] == "deleted";
        report.Checks.Add(new ValidationCheck("entity_deleted", "true",
            wasDeleted ? "true" : "false", wasDeleted, 3));
    }

    private async Task ValidatePayroll(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        // Verify transaction is actually accessible via GET
        // SalaryTransaction fields: id, version, date, year, month, payslips (NOT employee)
        try
        {
            var txn = await api.GetAsync($"/salary/transaction/{result.EntityId}",
                new Dictionary<string, string> { ["fields"] = "id,date,year,month,payslips" });
            var val = txn.GetProperty("value");

            report.Checks.Add(new ValidationCheck("salary_transaction_found", "true", "true", true, 2));

            // Check employee link and payslip from inline payslips array
            bool hasEmployee = false;
            int payslipCount = 0;
            if (val.TryGetProperty("payslips", out var payslips) && payslips.ValueKind == JsonValueKind.Array)
            {
                payslipCount = payslips.GetArrayLength();
                foreach (var ps in payslips.EnumerateArray())
                {
                    if (ps.TryGetProperty("employee", out var empRef) && empRef.ValueKind == JsonValueKind.Object
                        && empRef.TryGetProperty("id", out var empId) && empId.GetInt64() > 0)
                    {
                        hasEmployee = true;
                        break;
                    }
                }
            }

            report.Checks.Add(new ValidationCheck("has_employee_link", "true",
                hasEmployee ? "true" : "false", hasEmployee, 2));
            report.Checks.Add(new ValidationCheck("payslip_generated", "> 0",
                payslipCount.ToString(), payslipCount > 0, 2));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Payroll validation: GET transaction failed: {Msg}", ex.Message);
            report.Checks.Add(new ValidationCheck("salary_transaction_found", "true", "false", false, 2));
        }
    }

    // --- Helper methods ---

    private static void CheckStringField(JsonElement apiValue, Dictionary<string, object> entity,
        string field, ValidationReport report, int points)
    {
        if (!entity.TryGetValue(field, out var expectedObj)) return;

        var expected = expectedObj is JsonElement je ? je.GetString() : expectedObj?.ToString();
        if (string.IsNullOrEmpty(expected)) return;

        string? actual = null;
        if (apiValue.TryGetProperty(field, out var prop))
        {
            actual = prop.ValueKind == JsonValueKind.String ? prop.GetString()
                : prop.ValueKind == JsonValueKind.Number ? prop.GetRawText()
                : prop.GetRawText();
        }

        var passed = string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);
        report.Checks.Add(new ValidationCheck(field, expected ?? "", actual ?? "(null)", passed, points));
    }

    private static void CheckDecimalField(JsonElement apiValue, Dictionary<string, object> entity,
        string field, ValidationReport report, int points)
    {
        if (!entity.TryGetValue(field, out var expectedObj)) return;

        decimal expectedVal = 0;
        if (expectedObj is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number) expectedVal = je.GetDecimal();
            else decimal.TryParse(je.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out expectedVal);
        }
        else
        {
            decimal.TryParse(expectedObj?.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out expectedVal);
        }

        decimal actualVal = 0;
        if (apiValue.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.Number)
            actualVal = prop.GetDecimal();

        var passed = expectedVal == actualVal;
        report.Checks.Add(new ValidationCheck(field, expectedVal.ToString(), actualVal.ToString(), passed, points));
    }
}

public class ValidationReport
{
    public string TaskType { get; set; } = "";
    public string? EntityType { get; set; }
    public long? EntityId { get; set; }
    public List<ValidationCheck> Checks { get; set; } = new();
    public int PointsEarned { get; set; }
    public int MaxPoints { get; set; }
    public double Correctness { get; set; }
    public string? Error { get; set; }

    public void CalculateScore()
    {
        MaxPoints = Checks.Sum(c => c.Points);
        PointsEarned = Checks.Where(c => c.Passed).Sum(c => c.Points);
        Correctness = MaxPoints > 0 ? (double)PointsEarned / MaxPoints : 0;
    }
}

public record ValidationCheck(
    string Field,
    string Expected,
    string Actual,
    bool Passed,
    int Points = 1
);
