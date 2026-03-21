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
                case "reminder_fee":
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
                case "correct_ledger":
                    await ValidateLedgerCorrection(api, extracted, handlerResult, report);
                    break;
                case "annual_accounts":
                    await ValidateAnnualAccounts(api, extracted, handlerResult, report);
                    break;
                case "delete_entity":
                    await ValidateDelete(api, handlerResult, report);
                    break;
                case "run_payroll":
                    await ValidatePayroll(api, extracted, handlerResult, report);
                    break;
                case "bank_reconciliation":
                    await ValidateBankReconciliation(api, extracted, handlerResult, report);
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
            new Dictionary<string, string> { ["fields"] = "id,firstName,lastName,email,dateOfBirth,phoneNumberMobile,department(id,name)" });
        var val = emp.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("employee") ?? new();
        var departmentEntity = extracted.Entities.GetValueOrDefault("department") ?? new();

        // Employee found (1 point) — competition: 7 checks / 8pts = ~1.14/check
        report.Checks.Add(new ValidationCheck("employee_found", "true", "true", true, 1));

        // First name (1 point)
        CheckStringField(val, entity, "firstName", report, 1);

        // Last name (1 point)
        CheckStringField(val, entity, "lastName", report, 1);

        // Email (1 point)
        CheckStringField(val, entity, "email", report, 1);

        // Date of birth (optional, 1 point)
        if (entity.ContainsKey("dateOfBirth"))
            CheckStringField(val, entity, "dateOfBirth", report, 1);

        // Phone (1 point)
        if (entity.ContainsKey("phoneNumberMobile"))
            CheckStringField(val, entity, "phoneNumberMobile", report, 1);

        if (departmentEntity.TryGetValue("name", out var departmentNameObj))
        {
            var expectedDepartment = departmentNameObj?.ToString();
            var actualDepartment = val.TryGetProperty("department", out var departmentProp)
                && departmentProp.ValueKind == JsonValueKind.Object
                && departmentProp.TryGetProperty("name", out var departmentNameProp)
                ? departmentNameProp.GetString()
                : null;

            report.Checks.Add(new ValidationCheck("department", expectedDepartment ?? "",
                actualDepartment ?? "(null)", string.Equals(expectedDepartment?.Trim(), actualDepartment?.Trim(), StringComparison.OrdinalIgnoreCase), 1));
        }

        await ValidateEmployeeEmploymentAsync(api, result.EntityId.Value, entity, report);

        // Administrator role (2 points) — competition: ~2pts out of 8 total
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
                    hasEntitlements ? "true" : "false", hasEntitlements, 2));
            }
            catch
            {
                report.Checks.Add(new ValidationCheck("administrator_role", "true", "unknown", false, 2));
            }
        }
    }

    private async Task ValidateEmployeeEmploymentAsync(TripletexApiClient api, long employeeId, Dictionary<string, object> entity, ValidationReport report)
    {
        var needsEmploymentValidation = entity.ContainsKey("startDate")
            || entity.ContainsKey("occupationCode")
            || entity.ContainsKey("jobCode")
            || entity.ContainsKey("employmentPercentage")
            || entity.ContainsKey("percentageOfFullTimeEquivalent")
            || entity.ContainsKey("annualSalary")
            || entity.ContainsKey("employmentType")
            || entity.ContainsKey("employmentForm")
            || entity.ContainsKey("salaryType")
            || entity.ContainsKey("remunerationType");

        if (!needsEmploymentValidation)
            return;

        var expectedStartDate = GetStringFromEntity(entity, "startDate");
        var employmentResponse = await api.GetAsync("/employee/employment", new Dictionary<string, string>
        {
            ["employeeId"] = employeeId.ToString(),
            ["count"] = "20",
            ["fields"] = "id,startDate,employmentDetails(date,occupationCode(code),percentageOfFullTimeEquivalent,annualSalary,employmentType,employmentForm,remunerationType)"
        });

        if (!employmentResponse.TryGetProperty("values", out var employments) || employments.GetArrayLength() == 0)
        {
            report.Checks.Add(new ValidationCheck("employment_found", "true", "false", false, 1));
            return;
        }

        JsonElement? matchingEmployment = null;
        foreach (var employment in employments.EnumerateArray())
        {
            var actualStartDate = employment.TryGetProperty("startDate", out var startDateProp) ? startDateProp.GetString() : null;
            if (expectedStartDate == null || string.Equals(expectedStartDate, actualStartDate, StringComparison.OrdinalIgnoreCase))
            {
                matchingEmployment = employment;
                break;
            }
        }

        matchingEmployment ??= employments[0];
        var selectedEmployment = matchingEmployment.Value;

        report.Checks.Add(new ValidationCheck("employment_found", "true", "true", true, 1));

        if (expectedStartDate != null)
        {
            var actualStartDate = selectedEmployment.TryGetProperty("startDate", out var startDateProp) ? startDateProp.GetString() : null;
            report.Checks.Add(new ValidationCheck("employment.startDate", expectedStartDate,
                actualStartDate ?? "(null)", string.Equals(expectedStartDate, actualStartDate, StringComparison.OrdinalIgnoreCase), 1));
        }

        if (!selectedEmployment.TryGetProperty("employmentDetails", out var detailsArray)
            || detailsArray.ValueKind != JsonValueKind.Array
            || detailsArray.GetArrayLength() == 0)
        {
            report.Checks.Add(new ValidationCheck("employment.details_found", "true", "false", false, 1));
            return;
        }

        var details = detailsArray[detailsArray.GetArrayLength() - 1];
        report.Checks.Add(new ValidationCheck("employment.details_found", "true", "true", true, 1));

        var expectedOccupationCode = GetStringFromEntity(entity, "occupationCode") ?? GetStringFromEntity(entity, "jobCode");
        if (!string.IsNullOrWhiteSpace(expectedOccupationCode))
        {
            var actualOccupationCode = details.TryGetProperty("occupationCode", out var occupationProp)
                && occupationProp.ValueKind == JsonValueKind.Object
                && occupationProp.TryGetProperty("code", out var codeProp)
                ? codeProp.GetString()
                : null;
            report.Checks.Add(new ValidationCheck("employment.occupationCode", expectedOccupationCode,
                actualOccupationCode ?? "(null)", string.Equals(expectedOccupationCode, actualOccupationCode, StringComparison.OrdinalIgnoreCase), 1));
        }

        var expectedPercentage = GetDecimalFromEntity(entity, "percentageOfFullTimeEquivalent") ?? GetDecimalFromEntity(entity, "employmentPercentage");
        if (expectedPercentage.HasValue)
        {
            var actualPercentage = details.TryGetProperty("percentageOfFullTimeEquivalent", out var percentageProp) ? percentageProp.GetDecimal() : 0m;
            report.Checks.Add(new ValidationCheck("employment.percentageOfFullTimeEquivalent", expectedPercentage.Value.ToString("0.##"),
                actualPercentage.ToString("0.##"), expectedPercentage.Value == actualPercentage, 1));
        }

        var expectedAnnualSalary = GetDecimalFromEntity(entity, "annualSalary");
        if (expectedAnnualSalary.HasValue)
        {
            var actualAnnualSalary = details.TryGetProperty("annualSalary", out var salaryProp) ? salaryProp.GetDecimal() : 0m;
            report.Checks.Add(new ValidationCheck("employment.annualSalary", expectedAnnualSalary.Value.ToString("0.##"),
                actualAnnualSalary.ToString("0.##"), expectedAnnualSalary.Value == actualAnnualSalary, 1));
        }

        var expectedEmploymentType = NormalizeEmploymentType(GetStringFromEntity(entity, "employmentType"));
        if (!string.IsNullOrWhiteSpace(expectedEmploymentType))
        {
            var actualEmploymentType = details.TryGetProperty("employmentType", out var employmentTypeProp) ? employmentTypeProp.GetString() : null;
            report.Checks.Add(new ValidationCheck("employment.employmentType", expectedEmploymentType,
                actualEmploymentType ?? "(null)", string.Equals(expectedEmploymentType, actualEmploymentType, StringComparison.OrdinalIgnoreCase), 1));
        }

        var expectedEmploymentForm = NormalizeEmploymentForm(GetStringFromEntity(entity, "employmentForm") ?? GetStringFromEntity(entity, "employmentType"));
        if (!string.IsNullOrWhiteSpace(expectedEmploymentForm))
        {
            var actualEmploymentForm = details.TryGetProperty("employmentForm", out var employmentFormProp) ? employmentFormProp.GetString() : null;
            report.Checks.Add(new ValidationCheck("employment.employmentForm", expectedEmploymentForm,
                actualEmploymentForm ?? "(null)", string.Equals(expectedEmploymentForm, actualEmploymentForm, StringComparison.OrdinalIgnoreCase), 1));
        }

        var expectedRemunerationType = NormalizeRemunerationType(GetStringFromEntity(entity, "remunerationType") ?? GetStringFromEntity(entity, "salaryType"));
        if (!string.IsNullOrWhiteSpace(expectedRemunerationType))
        {
            var actualRemunerationType = details.TryGetProperty("remunerationType", out var remunerationTypeProp) ? remunerationTypeProp.GetString() : null;
            report.Checks.Add(new ValidationCheck("employment.remunerationType", expectedRemunerationType,
                actualRemunerationType ?? "(null)", string.Equals(expectedRemunerationType, actualRemunerationType, StringComparison.OrdinalIgnoreCase), 1));
        }
    }

    private async Task ValidateCustomer(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var cust = await api.GetAsync($"/customer/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "id,name,email,organizationNumber,phoneNumber,physicalAddress(*),postalAddress(*)" });
        var val = cust.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("customer")
            ?? extracted.Entities.GetValueOrDefault("customer1") ?? new();

        // Competition: 7 checks / 8pts
        report.Checks.Add(new ValidationCheck("customer_found", "true", "true", true, 1));
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

        // Competition: 5 checks / 7pts
        report.Checks.Add(new ValidationCheck("product_found", "true", "true", true, 1));
        CheckStringField(val, entity, "name", report, 2);

        // LLM may extract as "number" or "productNumber"
        var numberKey = entity.ContainsKey("number") ? "number" : entity.ContainsKey("productNumber") ? "productNumber" : null;
        if (numberKey != null)
        {
            var normalizedForNumber = new Dictionary<string, object>(entity) { ["number"] = entity[numberKey] };
            CheckStringField(val, normalizedForNumber, "number", report, 2);
        }

        // Check price — LLM may extract as price, unitPrice, priceExcludingVAT, or priceExcludingVatCurrency
        var priceKey = new[] { "priceExcludingVatCurrency", "price", "unitPrice", "priceExcludingVAT" }
            .FirstOrDefault(k => entity.ContainsKey(k));
        if (priceKey != null)
        {
            // Normalize entity key to API field name for comparison
            var normalizedEntity = new Dictionary<string, object>(entity) { ["priceExcludingVatCurrency"] = entity[priceKey] };
            CheckDecimalField(val, normalizedEntity, "priceExcludingVatCurrency", report, 2);
        }

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

        // Competition: 3 checks / 7pts
        report.Checks.Add(new ValidationCheck("department_found", "true", "true", true, 2));
        CheckStringField(val, entity, "name", report, 3);
        CheckStringField(val, entity, "departmentNumber", report, 2);
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

        // Check 1: invoice_found (2pts)
        report.Checks.Add(new ValidationCheck("invoice_found", "true", "true", true, 2));

        // Check 2: has_customer (1pt)
        if (val.TryGetProperty("customer", out var custRef) && custRef.ValueKind == JsonValueKind.Object
            && custRef.TryGetProperty("id", out var custId) && custId.GetInt64() > 0)
            report.Checks.Add(new ValidationCheck("has_customer", "true", "true", true, 1));
        else
            report.Checks.Add(new ValidationCheck("has_customer", "true", "false", false, 1));

        // Check 3: has_amount (1pt)
        decimal invoiceAmount = 0;
        if (val.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number)
        {
            invoiceAmount = amount.GetDecimal();
            report.Checks.Add(new ValidationCheck("has_amount", "> 0", invoiceAmount.ToString(), invoiceAmount > 0, 1));
        }
        else
        {
            report.Checks.Add(new ValidationCheck("has_amount", "> 0", "0", false, 1));
        }

        // Check 4: has_order_lines — verify invoice has line items via order
        bool hasOrderLines = false;
        if (val.TryGetProperty("orders", out var ordersRef) && ordersRef.ValueKind == JsonValueKind.Object
            && ordersRef.TryGetProperty("listDTO", out var orderList) && orderList.ValueKind == JsonValueKind.Array)
        {
            hasOrderLines = orderList.GetArrayLength() > 0;
        }
        else if (result.ExtraIds.TryGetValue("orderId", out var orderId))
        {
            try
            {
                var order = await api.GetAsync($"/order/{orderId}",
                    new Dictionary<string, string> { ["fields"] = "id,orderLines" });
                var orderVal = order.GetProperty("value");
                if (orderVal.TryGetProperty("orderLines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    hasOrderLines = lines.GetArrayLength() > 0;
            }
            catch { /* best effort */ }
        }
        else
        {
            // Assume lines exist if amount > 0
            hasOrderLines = invoiceAmount > 0;
        }
        report.Checks.Add(new ValidationCheck("has_order_lines", "true", hasOrderLines ? "true" : "false", hasOrderLines, 1));

        // Check 5: correct_amount — verify amount matches extracted expected values
        var invoiceEntity = extracted.Entities.GetValueOrDefault("invoice") ?? new();
        var orderEntity = extracted.Entities.GetValueOrDefault("order") ?? new();
        decimal? expectedAmount = GetDecimalFromEntity(invoiceEntity, "amount")
            ?? GetDecimalFromEntity(invoiceEntity, "totalAmount")
            ?? GetDecimalFromEntity(orderEntity, "totalAmount");

        if (expectedAmount.HasValue && expectedAmount.Value > 0)
        {
            bool amountOk = Math.Abs(invoiceAmount - expectedAmount.Value) < 1m;
            report.Checks.Add(new ValidationCheck("correct_amount", expectedAmount.Value.ToString("F2"),
                invoiceAmount.ToString("F2"), amountOk, 1));
        }

        // Check 6: invoice_sent — check if invoice was sent/dispatched
        if (val.TryGetProperty("isSent", out var isSent))
        {
            report.Checks.Add(new ValidationCheck("invoice_sent", "true",
                isSent.ValueKind == JsonValueKind.True ? "true" : "false",
                isSent.ValueKind == JsonValueKind.True, 1));
        }
    }

    private async Task ValidatePayment(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var inv = await api.GetAsync($"/invoice/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = inv.GetProperty("value");

        // Check 1: invoice_found (2pts)
        report.Checks.Add(new ValidationCheck("invoice_found", "true", "true", true, 2));

        // Check 2: payment_registered / payment_reversed (2pts)
        if (val.TryGetProperty("amountOutstanding", out var outstanding))
        {
            var outstandingVal = outstanding.GetDecimal();
            if (extracted.Action == "reverse")
            {
                report.Checks.Add(new ValidationCheck("payment_reversed", "> 0",
                    outstandingVal.ToString(), outstandingVal > 0, 2));
            }
            else
            {
                report.Checks.Add(new ValidationCheck("payment_registered", "0",
                    outstandingVal.ToString(), outstandingVal == 0, 2));
            }
        }

        // Check 3: correct_paid_amount — verify the paid amount matches invoice total (2pts)
        if (val.TryGetProperty("amount", out var invAmount) && invAmount.ValueKind == JsonValueKind.Number)
        {
            var totalAmount = invAmount.GetDecimal();
            var amountPaid = val.TryGetProperty("amountOutstanding", out var os) && os.ValueKind == JsonValueKind.Number
                ? totalAmount - os.GetDecimal() : 0;

            if (extracted.Action != "reverse")
            {
                bool paidCorrect = Math.Abs(amountPaid - totalAmount) < 1m;
                report.Checks.Add(new ValidationCheck("correct_paid_amount",
                    totalAmount.ToString("F2"), amountPaid.ToString("F2"), paidCorrect, 2));
            }
        }

        // Check 4: has_customer — invoice has customer reference (1pt)
        bool hasCust = val.TryGetProperty("customer", out var custRef) && custRef.ValueKind == JsonValueKind.Object
            && custRef.TryGetProperty("id", out var custId) && custId.GetInt64() > 0;
        report.Checks.Add(new ValidationCheck("has_customer", "true", hasCust ? "true" : "false", hasCust, 1));

        // Check 5: has_amount — invoice has a valid total amount (1pt)
        decimal invoiceAmt = val.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number
            ? amt.GetDecimal() : 0;
        report.Checks.Add(new ValidationCheck("has_amount", "> 0", invoiceAmt.ToString(), invoiceAmt > 0, 1));
    }

    private async Task ValidateProject(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var proj = await api.GetAsync($"/project/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "id,name,customer,projectManager,isFixedPrice,fixedprice,invoicingPlan,preliminaryInvoice" });
        var val = proj.GetProperty("value");

        var entity = extracted.Entities.GetValueOrDefault("project") ?? new();

        report.Checks.Add(new ValidationCheck("project_found", "true", "true", true, 2));
        CheckStringField(val, entity, "name", report, 2);

        if (val.TryGetProperty("customer", out var custRef) && custRef.ValueKind == JsonValueKind.Object
            && custRef.TryGetProperty("id", out var custId) && custId.GetInt64() > 0)
            report.Checks.Add(new ValidationCheck("has_customer", "true", "true", true, 2));

        if (val.TryGetProperty("projectManager", out var pmRef) && pmRef.ValueKind == JsonValueKind.Object
            && pmRef.TryGetProperty("id", out var pmId) && pmId.GetInt64() > 0)
            report.Checks.Add(new ValidationCheck("has_project_manager", "true", "true", true, 2));

        // Check isFixedPrice if fixedPrice was requested
        bool fixedPriceRequested = entity.ContainsKey("fixedPrice") || entity.ContainsKey("fixedprice");
        if (fixedPriceRequested)
        {
            bool isFixed = val.TryGetProperty("isFixedPrice", out var fp) && fp.ValueKind == JsonValueKind.True;
            report.Checks.Add(new ValidationCheck("isFixedPrice", "true", isFixed.ToString().ToLower(), isFixed, 2));
        }

        // Check if invoice was created for the project (invoicingPlan, preliminaryInvoice, or /invoice?projectId)
        var invoiceEntity = extracted.Entities.GetValueOrDefault("invoice");
        if (invoiceEntity != null && invoiceEntity.Count > 0)
        {
            bool hasInvoice = false;
            if (val.TryGetProperty("invoicingPlan", out var plan) && plan.ValueKind == JsonValueKind.Array && plan.GetArrayLength() > 0)
                hasInvoice = true;
            else if (val.TryGetProperty("preliminaryInvoice", out var pre) && pre.ValueKind == JsonValueKind.Object
                && pre.TryGetProperty("id", out var preId) && preId.GetInt64() > 0)
                hasInvoice = true;
            else
            {
                // Check via /order?projectId=X (order-linked to project gives invoice; requires date range)
                try
                {
                    var orderSearch = await api.GetAsync("/order", new Dictionary<string, string>
                    {
                        ["projectId"] = result.EntityId.Value.ToString(),
                        ["count"] = "5",
                        ["fields"] = "id",
                        ["orderDateFrom"] = "2020-01-01",
                        ["orderDateTo"] = "2030-12-31"
                    });
                    if (orderSearch.TryGetProperty("values", out var ordVals) && ordVals.GetArrayLength() > 0)
                        hasInvoice = true;
                }
                catch { }

                if (!hasInvoice)
                {
                    // Fallback: /invoice?projectId=X with broad date range
                    try
                    {
                        var invSearch = await api.GetAsync("/invoice", new Dictionary<string, string>
                        {
                            ["projectId"] = result.EntityId.Value.ToString(),
                            ["invoiceDateFrom"] = "2020-01-01",
                            ["invoiceDateTo"] = "2030-12-31",
                            ["count"] = "5",
                            ["fields"] = "id"
                        });
                        if (invSearch.TryGetProperty("values", out var invVals) && invVals.GetArrayLength() > 0)
                            hasInvoice = true;
                    }
                    catch { }
                }
            }
            report.Checks.Add(new ValidationCheck("has_project_invoice", "true", hasInvoice.ToString().ToLower(), hasInvoice, 2));
        }
    }

    private async Task ValidateTravelExpense(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var te = await api.GetAsync($"/travelExpense/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "*" });
        var val = te.GetProperty("value");

        // Check 1: travel_expense_found (1pt)
        report.Checks.Add(new ValidationCheck("travel_expense_found", "true", "true", true, 1));

        // Check 2: has_title (1pt)
        bool hasTitle = val.TryGetProperty("title", out var title) && !string.IsNullOrEmpty(title.GetString());
        report.Checks.Add(new ValidationCheck("has_title", "true", hasTitle ? "true" : "false", hasTitle, 1));

        // Check 3: has_employee (2pt)
        bool hasEmployee = val.TryGetProperty("employee", out var empRef) && empRef.ValueKind == JsonValueKind.Object
            && empRef.TryGetProperty("id", out var empId) && empId.GetInt64() > 0;
        report.Checks.Add(new ValidationCheck("has_employee", "true", hasEmployee ? "true" : "false", hasEmployee, 2));

        // Check 4 & 5: has_costs + correct_cost_count (1pt each)
        try
        {
            var costs = await api.GetAsync("/travelExpense/cost",
                new Dictionary<string, string>
                {
                    ["travelExpenseId"] = result.EntityId.Value.ToString(),
                    ["count"] = "100",
                    ["fields"] = "id,amount,date,costCategory"
                });
            if (costs.TryGetProperty("values", out var costVals))
            {
                var costCount = costVals.GetArrayLength();
                report.Checks.Add(new ValidationCheck("has_costs", "> 0",
                    costCount.ToString(), costCount > 0, 1));

                // Count expected costs from extraction
                var teEntity = extracted.Entities.GetValueOrDefault("travelExpense")
                    ?? extracted.Entities.GetValueOrDefault("travel_expense") ?? new();
                int expectedCosts = 0;
                // Count cost entities
                foreach (var key in extracted.Entities.Keys)
                {
                    if (key.StartsWith("cost", StringComparison.OrdinalIgnoreCase)
                        || key.StartsWith("expense", StringComparison.OrdinalIgnoreCase))
                        expectedCosts++;
                }
                if (expectedCosts > 0)
                {
                    bool countOk = costCount >= expectedCosts;
                    report.Checks.Add(new ValidationCheck("correct_cost_count", expectedCosts.ToString(),
                        costCount.ToString(), countOk, 1));
                }
            }
        }
        catch { /* cost check is best-effort */ }

        // Check 6: has_dates — verify departure/return dates (2pt)
        var teEntity2 = extracted.Entities.GetValueOrDefault("travelExpense")
            ?? extracted.Entities.GetValueOrDefault("travel_expense") ?? new();
        if (teEntity2.ContainsKey("departureDate") || teEntity2.ContainsKey("returnDate"))
        {
            bool hasDeparture = val.TryGetProperty("departureDate", out var depDate)
                && !string.IsNullOrEmpty(depDate.GetString());
            bool hasReturn = val.TryGetProperty("returnDate", out var retDate)
                && !string.IsNullOrEmpty(retDate.GetString());
            report.Checks.Add(new ValidationCheck("has_dates", "true",
                (hasDeparture || hasReturn) ? "true" : "false", hasDeparture || hasReturn, 2));
        }
    }

    private async Task ValidateCreditNote(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        // The handler sets EntityId to the original invoice ID.
        // The credit note is a separate invoice — search for it via the original invoice.
        long originalInvoiceId = result.EntityId.Value;

        // Try to find the credit note invoice by searching for invoices that reference the original
        // Credit notes in Tripletex are invoices with negative amounts linked to the original
        try
        {
            // First, verify the original invoice exists and get its details
            var origInv = await api.GetAsync($"/invoice/{originalInvoiceId}",
                new Dictionary<string, string> { ["fields"] = "id,customer,amount,amountOutstanding" });
            var origVal = origInv.GetProperty("value");

            // Search for credit notes (invoices with isCreditNote=true or negative amounts)
            // The credit note should have been created after our handler ran
            var searchParams = new Dictionary<string, string>
            {
                ["invoiceDateFrom"] = "2020-01-01",
                ["invoiceDateTo"] = "2030-12-31",
                ["count"] = "50",
                ["fields"] = "id,customer,amount,isCreditNote,invoiceNumber"
            };

            // Try to find it via customer ID
            if (origVal.TryGetProperty("customer", out var custRef) && custRef.ValueKind == JsonValueKind.Object
                && custRef.TryGetProperty("id", out var custIdProp))
            {
                searchParams["customerId"] = custIdProp.GetRawText();
            }

            var searchResult = await api.GetAsync("/invoice", searchParams);
            JsonElement? creditNote = null;

            if (searchResult.TryGetProperty("values", out var invoices) && invoices.ValueKind == JsonValueKind.Array)
            {
                foreach (var inv in invoices.EnumerateArray())
                {
                    // Credit notes have isCreditNote=true or negative amount, and different ID from original
                    if (inv.TryGetProperty("id", out var idProp) && idProp.GetInt64() != originalInvoiceId)
                    {
                        bool isCreditNote = inv.TryGetProperty("isCreditNote", out var cnFlag) && cnFlag.ValueKind == JsonValueKind.True;
                        bool hasNegativeAmount = inv.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number && amt.GetDecimal() < 0;
                        if (isCreditNote || hasNegativeAmount)
                        {
                            creditNote = inv;
                            break;
                        }
                    }
                }
            }

            if (creditNote.HasValue)
            {
                var cn = creditNote.Value;
                // Check 1: credit_note_found
                report.Checks.Add(new ValidationCheck("credit_note_found", "true", "true", true, 2));

                // Check 2: has_customer
                bool hasCust = cn.TryGetProperty("customer", out var cnCust) && cnCust.ValueKind == JsonValueKind.Object
                    && cnCust.TryGetProperty("id", out var cnCustId) && cnCustId.GetInt64() > 0;
                report.Checks.Add(new ValidationCheck("has_customer", "true", hasCust ? "true" : "false", hasCust, 2));

                // Check 3: has_amount
                bool hasAmount = cn.TryGetProperty("amount", out var cnAmt) && cnAmt.ValueKind == JsonValueKind.Number && cnAmt.GetDecimal() != 0;
                report.Checks.Add(new ValidationCheck("has_amount", "!= 0", cnAmt.GetRawText(), hasAmount, 1));

                // Check 4: correct_amount (should match negated original amount)
                if (origVal.TryGetProperty("amount", out var origAmt) && origAmt.ValueKind == JsonValueKind.Number)
                {
                    var expectedNeg = -origAmt.GetDecimal();
                    var actualAmt = cn.TryGetProperty("amount", out var ca) && ca.ValueKind == JsonValueKind.Number ? ca.GetDecimal() : 0;
                    bool amountOk = Math.Abs(actualAmt - expectedNeg) < 1m;
                    report.Checks.Add(new ValidationCheck("correct_amount", expectedNeg.ToString("F2"), actualAmt.ToString("F2"), amountOk, 2));
                }

                // Check 5: linked to original invoice
                report.Checks.Add(new ValidationCheck("has_linked_invoice", "true", "true", true, 1));
            }
            else
            {
                // Credit note not found — fail all checks
                report.Checks.Add(new ValidationCheck("credit_note_found", "true", "false", false, 2));
                report.Checks.Add(new ValidationCheck("has_customer", "true", "unknown", false, 2));
                report.Checks.Add(new ValidationCheck("has_amount", "!= 0", "0", false, 1));
                report.Checks.Add(new ValidationCheck("correct_amount", "expected", "unknown", false, 2));
                report.Checks.Add(new ValidationCheck("has_linked_invoice", "true", "false", false, 1));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CreditNote validation failed");
            report.Checks.Add(new ValidationCheck("credit_note_found", "true", "error", false, 2));
        }
    }

    private async Task ValidateVoucher(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue) return;

        var voucher = await api.GetAsync($"/ledger/voucher/{result.EntityId}",
            new Dictionary<string, string> { ["fields"] = "id,description,date,postings(id,amountGross,amountCurrency,amount,account(id,number,name),department(id,name))" });
        var val = voucher.GetProperty("value");

        // Check 1: voucher_found (2pts)
        report.Checks.Add(new ValidationCheck("voucher_found", "true", "true", true, 2));

        // Check 2: has_description (2pts)
        if (val.TryGetProperty("description", out var desc) && !string.IsNullOrEmpty(desc.GetString()))
            report.Checks.Add(new ValidationCheck("has_description", "true", "true", true, 2));
        else
            report.Checks.Add(new ValidationCheck("has_description", "true", "false", false, 2));

        // Check 3: has_postings (>= 2) (2pts)
        int postingCount = 0;
        decimal debitSum = 0, creditSum = 0;
        if (val.TryGetProperty("postings", out var postings) && postings.ValueKind == JsonValueKind.Array)
        {
            postingCount = postings.GetArrayLength();
            foreach (var posting in postings.EnumerateArray())
            {
                if (posting.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number)
                {
                    var amt = a.GetDecimal();
                    if (amt > 0) debitSum += amt;
                    else creditSum += Math.Abs(amt);
                }
                else if (posting.TryGetProperty("amountGross", out var ag) && ag.ValueKind == JsonValueKind.Number)
                {
                    var amt = ag.GetDecimal();
                    if (amt > 0) debitSum += amt;
                    else creditSum += Math.Abs(amt);
                }
            }
        }
        report.Checks.Add(new ValidationCheck("has_postings", ">= 2",
            postingCount.ToString(), postingCount >= 2, 2));

        // Check 4: postings_balanced — debits should equal credits (2pts)
        bool balanced = postingCount >= 2 && Math.Abs(debitSum - creditSum) < 1m;
        report.Checks.Add(new ValidationCheck("postings_balanced", "true",
            balanced ? "true" : $"debit={debitSum:F2},credit={creditSum:F2}", balanced, 2));

        // Check 5: correct_accounts — verify account numbers match extracted values (2pts)
        var voucherEntity = extracted.Entities.GetValueOrDefault("voucher") ?? new();
        var postingEntities = new List<Dictionary<string, object>>();
        // Check for posting1, posting2 etc. in extracted entities
        foreach (var key in extracted.Entities.Keys)
        {
            if (key.StartsWith("posting", StringComparison.OrdinalIgnoreCase) || key.StartsWith("line", StringComparison.OrdinalIgnoreCase))
                postingEntities.Add(extracted.Entities[key]);
        }
        var expectedAccounts = new List<string>();
        foreach (var pe in postingEntities)
        {
            var expectedAcct = pe.TryGetValue("accountNumber", out var an) ? an?.ToString()
                : pe.TryGetValue("account", out var a) ? a?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(expectedAcct))
                expectedAccounts.Add(expectedAcct.Trim());
        }

        var voucherAccount = GetStringFromEntity(voucherEntity, "account") ?? GetStringFromEntity(voucherEntity, "accountNumber");
        if (!string.IsNullOrWhiteSpace(voucherAccount))
            expectedAccounts.Add(voucherAccount.Trim());
        if ((GetStringFromEntity(voucherEntity, "supplierName") ?? GetStringFromEntity(voucherEntity, "supplierOrgNumber")) != null)
            expectedAccounts.Add("2400");

        expectedAccounts = expectedAccounts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (expectedAccounts.Count > 0 && postings.ValueKind == JsonValueKind.Array)
        {
            bool accountsOk = true;
            foreach (var expectedAcct in expectedAccounts)
            {
                bool found = false;
                foreach (var posting in postings.EnumerateArray())
                {
                    if (posting.TryGetProperty("account", out var acctRef) && acctRef.ValueKind == JsonValueKind.Object
                        && acctRef.TryGetProperty("number", out var numProp))
                    {
                        if (numProp.GetRawText().Trim('"') == expectedAcct)
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (!found) accountsOk = false;
            }
            report.Checks.Add(new ValidationCheck("correct_accounts", "true",
                accountsOk ? "true" : "false", accountsOk, 2));
        }

        var expectedDepartment = GetStringFromEntity(extracted.Entities.GetValueOrDefault("department") ?? new(), "name");
        if (expectedDepartment == null)
        {
            var dimensionEntity = extracted.Entities.GetValueOrDefault("dimension") ?? new();
            var dimensionName = GetStringFromEntity(dimensionEntity, "name");
            if (dimensionName != null && dimensionName.Contains("department", StringComparison.OrdinalIgnoreCase))
            {
                expectedDepartment = GetStringFromEntity(voucherEntity, "dimensionValue");
                if (expectedDepartment == null && dimensionEntity.TryGetValue("values", out var valuesObj) && valuesObj is JsonElement valuesJe && valuesJe.ValueKind == JsonValueKind.Array)
                {
                    expectedDepartment = valuesJe.EnumerateArray().FirstOrDefault().GetString();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedDepartment) && postings.ValueKind == JsonValueKind.Array)
        {
            bool departmentFound = false;
            string actualDepartment = "missing";
            foreach (var posting in postings.EnumerateArray())
            {
                if (posting.TryGetProperty("department", out var departmentRef) && departmentRef.ValueKind == JsonValueKind.Object
                    && departmentRef.TryGetProperty("name", out var departmentNameProp))
                {
                    actualDepartment = departmentNameProp.GetString() ?? "";
                    if (string.Equals(actualDepartment, expectedDepartment, StringComparison.OrdinalIgnoreCase))
                    {
                        departmentFound = true;
                        break;
                    }
                }
            }

            report.Checks.Add(new ValidationCheck("has_department", expectedDepartment,
                departmentFound ? expectedDepartment : actualDepartment, departmentFound, 2));
        }

        // Check 6: has_correct_amount — verify the posting amounts match extracted values (3pts for high-value vouchers)
        decimal? expectedTotalAmount = GetDecimalFromEntity(voucherEntity, "amount")
            ?? GetDecimalFromEntity(voucherEntity, "totalAmount");
        if (expectedTotalAmount.HasValue && expectedTotalAmount.Value > 0)
        {
            bool amountOk = Math.Abs(debitSum - expectedTotalAmount.Value) < 1m
                || Math.Abs(creditSum - expectedTotalAmount.Value) < 1m;
            report.Checks.Add(new ValidationCheck("correct_amount", expectedTotalAmount.Value.ToString("F2"),
                debitSum.ToString("F2"), amountOk, 3));
        }
    }

    private async Task ValidateAnnualAccounts(TripletexApiClient api, ExtractionResult extracted, HandlerResult handlerResult, ValidationReport report)
    {
        // Check that at least one voucher was created (we use the first one as primary)
        if (handlerResult.EntityId == null)
        {
            report.Checks.Add(new ValidationCheck("vouchers_found", "at_least_1", "0", false, 4));
            return;
        }

        var allIds = new List<long> { handlerResult.EntityId.Value };
        allIds.AddRange(handlerResult.AdditionalEntityIds);
        int totalVouchers = allIds.Count;

        // Check: created at least 1 depreciation voucher
        report.Checks.Add(new ValidationCheck("depreciation_voucher_found", "≥1", totalVouchers.ToString(), totalVouchers >= 1, 3));

        // Check: multiple vouchers created (ideally 3 depreciation + 1 prepaid + 1 tax = 5)
        report.Checks.Add(new ValidationCheck("multiple_vouchers_found", "≥3", totalVouchers.ToString(), totalVouchers >= 3, 2));

        // Verify first voucher exists in Tripletex
        var voucherId = handlerResult.EntityId.Value;
        JsonElement voucher;
        try
        {
            var response = await api.GetAsync($"/ledger/voucher/{voucherId}",
                new Dictionary<string, string> { ["fields"] = "id,date,description,postings(id,amount,account(id,number))" });
            voucher = response.GetProperty("value");
        }
        catch
        {
            report.Checks.Add(new ValidationCheck("voucher_valid", "valid", "not_found", false, 3));
            return;
        }

        report.Checks.Add(new ValidationCheck("voucher_valid", "valid", "valid", true, 3));

        // Check first voucher has postings
        bool hasPostings = voucher.TryGetProperty("postings", out var postings) && postings.GetArrayLength() >= 2;
        report.Checks.Add(new ValidationCheck("has_postings", "≥2", postings.GetArrayLength().ToString(), hasPostings, 2));
    }

    private async Task ValidateLedgerCorrection(TripletexApiClient api, ExtractionResult extracted, HandlerResult handlerResult, ValidationReport report)
    {
        // Expect at least one correction voucher to have been created
        if (handlerResult.EntityId == null)
        {
            report.Checks.Add(new ValidationCheck("correction_vouchers_found", "≥1", "0", false, 4));
            return;
        }

        var allIds = new List<long> { handlerResult.EntityId.Value };
        allIds.AddRange(handlerResult.AdditionalEntityIds);

        report.Checks.Add(new ValidationCheck("correction_vouchers_found", "≥1", allIds.Count.ToString(), allIds.Count >= 1, 2));
        report.Checks.Add(new ValidationCheck("all_corrections_applied", "4", allIds.Count.ToString(), allIds.Count >= 4, 4));

        // Verify first correction voucher exists and has balanced postings
        var voucherId = handlerResult.EntityId.Value;
        try
        {
            var response = await api.GetAsync($"/ledger/voucher/{voucherId}", new Dictionary<string, string>
            { ["fields"] = "id,date,description,postings(id,amountGross,account(id,number))" });
            if (!response.TryGetProperty("value", out var voucher))
            {
                report.Checks.Add(new ValidationCheck("voucher_valid", "valid", "not_found", false, 2));
                return;
            }
            report.Checks.Add(new ValidationCheck("voucher_valid", "valid", "valid", true, 1));

            if (voucher.TryGetProperty("postings", out var postings))
            {
                var count = postings.GetArrayLength();
                decimal sum = 0m;
                foreach (var p in postings.EnumerateArray())
                    if (p.TryGetProperty("amountGross", out var ag) && ag.ValueKind == JsonValueKind.Number)
                        sum += ag.GetDecimal();
                report.Checks.Add(new ValidationCheck("postings_balanced", "0", sum.ToString("F2"), Math.Abs(sum) < 0.01m, 3));
                report.Checks.Add(new ValidationCheck("postings_count", "≥2", count.ToString(), count >= 2, 1));
            }
        }
        catch
        {
            report.Checks.Add(new ValidationCheck("voucher_valid", "valid", "error", false, 2));
        }
    }

    private static readonly Dictionary<string, string> _deleteEntityPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customer"] = "/customer",
        ["product"] = "/product",
        ["order"] = "/order",
        ["department"] = "/department",
        ["travelExpense"] = "/travelExpense",
        ["travel_expense"] = "/travelExpense",
        ["voucher"] = "/ledger/voucher",
        ["project"] = "/project",
        ["supplier"] = "/supplier",
    };

    private async Task ValidateDelete(TripletexApiClient api, HandlerResult result, ValidationReport report)
    {
        var wasDeleted = result.Metadata.ContainsKey("action") && result.Metadata["action"] == "deleted";
        if (!wasDeleted || !result.EntityId.HasValue || result.EntityType == null)
        {
            report.Checks.Add(new ValidationCheck("entity_deleted", "true", "false", false, 3));
            return;
        }

        // Verify deletion by attempting a GET — should return 404 or empty
        bool confirmedDeleted = false;
        if (_deleteEntityPaths.TryGetValue(result.EntityType, out var path))
        {
            try
            {
                var getResult = await api.GetAsync($"{path}/{result.EntityId}",
                    new Dictionary<string, string> { ["fields"] = "id" });
                // If we got a result, the entity still exists — delete may have failed
                var val = getResult.GetProperty("value");
                confirmedDeleted = val.ValueKind == JsonValueKind.Null;
            }
            catch
            {
                // 404 exception means deleted successfully
                confirmedDeleted = true;
            }
        }
        else
        {
            // Unknown entity type — trust the metadata flag
            confirmedDeleted = wasDeleted;
        }

        report.Checks.Add(new ValidationCheck("entity_deleted", "true",
            confirmedDeleted ? "true" : "false", confirmedDeleted, 3));
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
                new Dictionary<string, string> { ["fields"] = "id,date,year,month,payslips(id)" });
            var val = txn.GetProperty("value");

            report.Checks.Add(new ValidationCheck("salary_transaction_found", "true", "true", true, 2));

            // The payslips array in the transaction response is stubs (id+url only).
            // Competition validator fetches individual payslips to check employee link and amounts.
            // We must do the same: GET /salary/payslip/{id} per payslip stub.
            bool hasEmployee = false;
            int payslipCount = 0;
            decimal totalAmount = 0;

            if (val.TryGetProperty("payslips", out var payslipsArr) && payslipsArr.ValueKind == JsonValueKind.Array)
            {
                payslipCount = payslipsArr.GetArrayLength();

                foreach (var stubElem in payslipsArr.EnumerateArray())
                {
                    if (!stubElem.TryGetProperty("id", out var psIdProp)) continue;
                    var psId = psIdProp.GetInt64();
                    try
                    {
                        var psResult = await api.GetAsync($"/salary/payslip/{psId}",
                            new Dictionary<string, string> { ["fields"] = "id,employee,amount,grossAmount" });
                        if (psResult.TryGetProperty("value", out var ps))
                        {
                            if (ps.TryGetProperty("employee", out var empRef) && empRef.ValueKind == JsonValueKind.Object
                                && empRef.TryGetProperty("id", out var empId) && empId.GetInt64() > 0)
                            {
                                hasEmployee = true;
                            }
                            if (ps.TryGetProperty("grossAmount", out var grossAmountProp) && grossAmountProp.ValueKind == JsonValueKind.Number)
                                totalAmount += grossAmountProp.GetDecimal();
                            else if (ps.TryGetProperty("amount", out var amountProp) && amountProp.ValueKind == JsonValueKind.Number)
                                totalAmount += amountProp.GetDecimal();
                        }
                    }
                    catch (Exception exInner)
                    {
                        _logger.LogWarning("Payroll validation: GET /salary/payslip/{Id} failed: {Msg}", psId, exInner.Message);
                    }
                }
            }

            report.Checks.Add(new ValidationCheck("has_employee_link", "true",
                hasEmployee ? "true" : "false", hasEmployee, 2));
            report.Checks.Add(new ValidationCheck("payslip_generated", "> 0",
                payslipCount.ToString(), payslipCount > 0, 2));

            // correct_amount: sum of baseSalary + bonus from extraction vs actual payslip amount
            var payrollEntity = extracted.Entities.GetValueOrDefault("payroll")
                ?? extracted.Entities.GetValueOrDefault("salary")
                ?? new();
            decimal expectedBase = GetDecimalFromEntity(payrollEntity, "baseSalary")
                ?? GetDecimalFromEntity(payrollEntity, "amount") ?? 0;
            decimal expectedBonus = GetDecimalFromEntity(payrollEntity, "bonus") ?? 0;
            decimal expectedTotal = expectedBase + expectedBonus;

            if (expectedTotal > 0)
            {
                bool amountOk = totalAmount > 0 && Math.Abs(totalAmount - expectedTotal) < 1m;
                report.Checks.Add(new ValidationCheck("correct_amount",
                    expectedTotal.ToString("F0"),
                    totalAmount.ToString("F0"),
                    amountOk, 2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Payroll validation: GET transaction failed: {Msg}", ex.Message);
            report.Checks.Add(new ValidationCheck("salary_transaction_found", "true", "false", false, 2));
        }
    }

    private async Task ValidateBankReconciliation(TripletexApiClient api, ExtractionResult extracted,
        HandlerResult result, ValidationReport report)
    {
        if (!result.EntityId.HasValue)
        {
            report.Checks.Add(new ValidationCheck("reconciliation_found", "true", "false", false, 5));
            report.Checks.Add(new ValidationCheck("invoice_and_supplier_rows_matched", "true", "false", false, 5));
            return;
        }

        var reconciliation = await api.GetAsync($"/bank/reconciliation/{result.EntityId.Value}",
            new Dictionary<string, string>
            {
                ["fields"] = "id,account(number),accountingPeriod(id,start,end),bankAccountClosingBalanceCurrency"
            });
        var reconciliationValue = reconciliation.GetProperty("value");

        var reconciliationEntity = extracted.Entities.GetValueOrDefault("reconciliation") ?? new();
        var expectedAccountNumber = GetStringFromEntity(reconciliationEntity, "accountNumber")
            ?? GetStringFromEntity(reconciliationEntity, "account")
            ?? result.Metadata.GetValueOrDefault("accountNumber")
            ?? "1920";
        var expectedClosingBalance = GetDecimalFromEntity(reconciliationEntity, "closingBalance");

        var parsedTransactions = ParseBankTransactions(extracted.Files);
        if (!expectedClosingBalance.HasValue && parsedTransactions.Count > 0)
            expectedClosingBalance = parsedTransactions[^1].RunningBalance;

        var actualAccountNumber = reconciliationValue.TryGetProperty("account", out var accountRef)
            && accountRef.ValueKind == JsonValueKind.Object
            && accountRef.TryGetProperty("number", out var accountNumberProp)
                ? accountNumberProp.GetRawText().Trim('"')
                : "(missing)";
        var actualClosingBalance = reconciliationValue.TryGetProperty("bankAccountClosingBalanceCurrency", out var balanceProp)
            && balanceProp.ValueKind == JsonValueKind.Number
                ? balanceProp.GetDecimal()
                : 0m;

        var reconciliationPassed = string.Equals(expectedAccountNumber, actualAccountNumber, StringComparison.OrdinalIgnoreCase)
            && (!expectedClosingBalance.HasValue || Math.Abs(expectedClosingBalance.Value - actualClosingBalance) < 1m);
        report.Checks.Add(new ValidationCheck(
            "reconciliation_found",
            $"account={expectedAccountNumber},balance={(expectedClosingBalance?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}",
            $"account={actualAccountNumber},balance={actualClosingBalance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}",
            reconciliationPassed,
            5));

        var expectedMatchableTransactions = parsedTransactions
            .Where(tx => tx.Type is BankReconciliationTransactionType.CustomerPayment or BankReconciliationTransactionType.SupplierPayment)
            .ToList();

        if (expectedMatchableTransactions.Count == 0)
        {
            report.Checks.Add(new ValidationCheck("invoice_and_supplier_rows_matched", ">= 0", "0", true, 5));
            return;
        }

        var matchesResult = await api.GetAsync("/bank/reconciliation/match", new Dictionary<string, string>
        {
            ["bankReconciliationId"] = result.EntityId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["count"] = "5000",
            ["fields"] = "id,type,transactions(id,description,amountCurrency,matched),postings(id,amount,amountGross,account(number))"
        });

        var matchedRows = 0;
        if (matchesResult.TryGetProperty("values", out var matchValues) && matchValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var expectedTx in expectedMatchableTransactions)
            {
                if (HasAcceptedBankMatch(matchValues, expectedTx))
                    matchedRows++;
            }
        }

        var rowsMatched = matchedRows == expectedMatchableTransactions.Count;
        report.Checks.Add(new ValidationCheck(
            "invoice_and_supplier_rows_matched",
            expectedMatchableTransactions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            matchedRows.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rowsMatched,
            5));
    }

    // --- Helper methods ---

    private static bool HasAcceptedBankMatch(JsonElement matchValues, ParsedBankTransaction expectedTx)
    {
        foreach (var match in matchValues.EnumerateArray())
        {
            if (!match.TryGetProperty("type", out var typeProp))
                continue;

            var matchType = typeProp.GetString() ?? string.Empty;
            if (!IsAcceptedBankMatchType(matchType))
                continue;

            if (!match.TryGetProperty("transactions", out var transactionsProp) || transactionsProp.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var tx in transactionsProp.EnumerateArray())
            {
                var actualDescription = tx.TryGetProperty("description", out var descProp)
                    ? descProp.GetString() ?? string.Empty
                    : string.Empty;
                var actualAmount = tx.TryGetProperty("amountCurrency", out var amountProp) && amountProp.ValueKind == JsonValueKind.Number
                    ? amountProp.GetDecimal()
                    : 0m;

                if (DescriptionsEquivalent(expectedTx.Description, actualDescription)
                    && Math.Abs(expectedTx.Amount - actualAmount) < 0.01m)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAcceptedBankMatchType(string matchType)
    {
        return string.Equals(matchType, "MANUAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(matchType, "APPROVED_SUGGESTION", StringComparison.OrdinalIgnoreCase)
            || string.Equals(matchType, "AUTO_MATCHED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(matchType, "AUTOPOSTING_APPROVED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DescriptionsEquivalent(string expected, string actual)
    {
        return NormalizeBankText(expected) == NormalizeBankText(actual);
    }

    private static string NormalizeBankText(string text)
    {
        var lowered = text.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.Replace(lowered, "\\s+", " ");
    }

    private static List<ParsedBankTransaction> ParseBankTransactions(List<SolveFile>? files)
    {
        var transactions = new List<ParsedBankTransaction>();
        if (files == null) return transactions;

        foreach (var file in files)
        {
            if (!file.Filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                !file.MimeType.Contains("csv", StringComparison.OrdinalIgnoreCase) &&
                !file.MimeType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var content = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(file.ContentBase64));
                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) continue;

                var header = lines[0].Trim();
                var delimiter = header.Contains(';') ? ';' : ',';

                for (var index = 1; index < lines.Length; index++)
                {
                    var line = lines[index].Trim();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split(delimiter);
                    if (parts.Length < 4)
                        continue;

                    var incomingText = parts.Length > 2 ? parts[2].Trim() : string.Empty;
                    var outgoingText = parts.Length > 3 ? parts[3].Trim() : string.Empty;
                    var balanceText = parts.Length > 4 ? parts[4].Trim() : string.Empty;

                    decimal amount = 0m;
                    if (!string.IsNullOrEmpty(incomingText) && decimal.TryParse(incomingText.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var incoming))
                        amount = incoming;
                    else if (!string.IsNullOrEmpty(outgoingText) && decimal.TryParse(outgoingText.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var outgoing))
                        amount = outgoing;

                    decimal runningBalance = 0m;
                    if (!string.IsNullOrEmpty(balanceText))
                        decimal.TryParse(balanceText.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out runningBalance);

                    var description = parts[1].Trim();
                    transactions.Add(new ParsedBankTransaction(description, amount, runningBalance, ClassifyBankTransaction(description)));
                }
            }
            catch
            {
                // Best-effort validator parsing only.
            }
        }

        return transactions;
    }

    private static BankReconciliationTransactionType ClassifyBankTransaction(string description)
    {
        var lower = description.ToLowerInvariant();
        if (lower.Contains("innbetaling") || lower.Contains("betaling fra"))
            return BankReconciliationTransactionType.CustomerPayment;
        if (lower.Contains("betaling leverand") || lower.Contains("betaling lieferant") || lower.Contains("betaling til"))
            return BankReconciliationTransactionType.SupplierPayment;
        if (lower.Contains("bankgebyr") || lower.Contains("bank fee"))
            return BankReconciliationTransactionType.BankFee;
        if (lower.Contains("skattetrekk") || lower.Contains("skatt"))
            return BankReconciliationTransactionType.Tax;
        if (lower.Contains("lønn") || lower.Contains("salary"))
            return BankReconciliationTransactionType.Salary;
        return BankReconciliationTransactionType.Other;
    }

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

    private static string? GetStringFromEntity(Dictionary<string, object> entity, string key)
    {
        if (!entity.TryGetValue(key, out var v)) return null;
        if (v is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => je.GetRawText().Trim('"')
            };
        }
        return v?.ToString();
    }

    private static string? NormalizeEmploymentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lower = value.Trim().ToLowerInvariant();
        if (lower.Contains("freelance")) return "FREELANCE";
        if (lower.Contains("maritime")) return "MARITIME";
        return "ORDINARY";
    }

    private static string? NormalizeEmploymentForm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lower = value.Trim().ToLowerInvariant();
        if (lower.Contains("temporary") || lower.Contains("midlertid") || lower.Contains("tempor")) return "TEMPORARY";
        if (lower.Contains("call")) return "TEMPORARY_ON_CALL";
        return "PERMANENT";
    }

    private static string? NormalizeRemunerationType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lower = value.Trim().ToLowerInvariant();
        if (lower.Contains("hour") || lower.Contains("time") || lower.Contains("timel")) return "HOURLY_WAGE";
        if (lower.Contains("commission") || lower.Contains("provis")) return "COMMISION_PERCENTAGE";
        if (lower.Contains("fee") || lower.Contains("honorar")) return "FEE";
        return "MONTHLY_WAGE";
    }

    private static decimal? GetDecimalFromEntity(Dictionary<string, object> entity, string key)
    {
        if (!entity.TryGetValue(key, out var v)) return null;
        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetDecimal();
            if (decimal.TryParse(je.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }
        if (decimal.TryParse(v?.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d2)) return d2;
        return null;
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

internal enum BankReconciliationTransactionType
{
    CustomerPayment,
    SupplierPayment,
    BankFee,
    Tax,
    Salary,
    Other
}

internal sealed record ParsedBankTransaction(
    string Description,
    decimal Amount,
    decimal RunningBalance,
    BankReconciliationTransactionType Type
);


