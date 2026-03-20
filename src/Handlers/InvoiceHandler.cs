using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class InvoiceHandler : ITaskHandler
{
    private readonly ILogger<InvoiceHandler> _logger;

    public InvoiceHandler(ILogger<InvoiceHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var (invoiceId, _) = await CreateInvoiceChainAsync(api, extracted);
        return new HandlerResult { EntityType = "invoice", EntityId = invoiceId };
    }

    public async Task<(long invoiceId, decimal amount)> CreateInvoiceChainAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Full chain: Customer (find or create) → Order (with OrderLines + VatType) → Invoice → optionally Send
        var cust = extracted.Entities.GetValueOrDefault("customer") ?? new();
        var invoice = extracted.Entities.GetValueOrDefault("invoice") ?? new();

        // Step 0+1+2: Ensure bank account, find/create customer, resolve VAT type — all in parallel
        var bankTask = EnsureBankAccount(api);
        var customerTask = ResolveOrCreateCustomer(api, cust, invoice, extracted);
        var vatTypeTask = ResolveVatTypes(api);
        await Task.WhenAll(bankTask, customerTask, vatTypeTask);
        var customerId = customerTask.Result;
        var (vatTypeId, vatTypesArray) = vatTypeTask.Result;
        var outputRateMap = BuildOutputRateMap(vatTypesArray);
        _logger.LogInformation("Using customer ID: {Id}", customerId);

        // Composite task: if project + timeRegistration/employee are present, create project, activity, and register timesheet hours
        var projectEntity = extracted.Entities.GetValueOrDefault("project");
        var employeeEntity = extracted.Entities.GetValueOrDefault("employee");
        var timeRegEntity = extracted.Entities.GetValueOrDefault("timeRegistration");

        // If timeRegistration contains employee info, extract it
        if (timeRegEntity != null && employeeEntity == null)
        {
            if (timeRegEntity.TryGetValue("employee", out var empVal) && empVal is JsonElement empJson && empJson.ValueKind == JsonValueKind.Object)
            {
                employeeEntity = new Dictionary<string, object>();
                foreach (var prop in empJson.EnumerateObject())
                    employeeEntity[prop.Name] = prop.Value;
            }
        }

        // If timeRegistration has activity, merge it into project entity
        if (timeRegEntity != null && projectEntity != null)
        {
            if (!projectEntity.ContainsKey("activity") && timeRegEntity.TryGetValue("activity", out var actVal))
                projectEntity["activity"] = actVal;
            if (!projectEntity.ContainsKey("hourlyRate") && timeRegEntity.TryGetValue("hourlyRate", out var hrVal))
                projectEntity["hourlyRate"] = hrVal;
            if (!projectEntity.ContainsKey("hours") && timeRegEntity.TryGetValue("hours", out var hrsVal))
                projectEntity["hours"] = hrsVal;
        }

        if (projectEntity != null && projectEntity.Count > 0 && employeeEntity != null && employeeEntity.Count > 0)
        {
            await CreateProjectAndRegisterHours(api, extracted, customerId, projectEntity, employeeEntity);
        }

        // Step 3: Build order lines with per-line VAT types
        var lines = BuildOrderLines(extracted, vatTypeId, outputRateMap);

        // Step 3.5: Create products for lines with product codes
        await CreateProductsForLines(api, lines, extracted, vatTypeId, outputRateMap);

        // Step 4: Create order
        var invoiceDate = GetStringField(invoice, "invoiceDate")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd"));
        var deliveryDate = GetStringField(invoice, "deliveryDate") ?? invoiceDate;

        var orderBody = new Dictionary<string, object>
        {
            ["customer"] = new { id = customerId },
            ["orderDate"] = invoiceDate,
            ["deliveryDate"] = deliveryDate,
            ["orderLines"] = lines
        };

        var orderResult = await api.PostAsync("/order", orderBody);
        var orderId = orderResult.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created order ID: {Id}", orderId);

        // Step 5: Create invoice
        var invoiceDueDate = GetStringField(invoice, "invoiceDueDate")
            ?? (extracted.Dates.Count > 1 ? extracted.Dates[1]
                : DateTime.Parse(invoiceDate).AddDays(30).ToString("yyyy-MM-dd"));

        var invoiceBody = new Dictionary<string, object>
        {
            ["invoiceDate"] = invoiceDate,
            ["invoiceDueDate"] = invoiceDueDate,
            ["orders"] = new[] { new { id = orderId } }
        };

        var invoiceResult = await api.PostAsync("/invoice", invoiceBody);
        var invoiceValue = invoiceResult.GetProperty("value");
        var invoiceId = invoiceValue.GetProperty("id").GetInt64();

        // Try to get amount from invoice response (multiple fields)
        var invoiceAmount = 0m;
        if (invoiceValue.TryGetProperty("amount", out var amtProp) && amtProp.ValueKind == JsonValueKind.Number)
            invoiceAmount = amtProp.GetDecimal();
        else if (invoiceValue.TryGetProperty("amountCurrency", out var amtCurr) && amtCurr.ValueKind == JsonValueKind.Number)
            invoiceAmount = amtCurr.GetDecimal();

        // Fallback: calculate from order lines or extracted amounts
        if (invoiceAmount == 0m)
        {
            // Sum from order lines we built
            foreach (var line in lines)
            {
                if (line.TryGetValue("unitPriceExcludingVatCurrency", out var u))
                {
                    var unit = Convert.ToDecimal(u, CultureInfo.InvariantCulture);
                    var count = line.TryGetValue("count", out var c) ? Convert.ToDecimal(c, CultureInfo.InvariantCulture) : 1m;
                    invoiceAmount += count * unit;
                }
            }
            // Fall back to sum of raw_amounts
            if (invoiceAmount == 0m)
            {
                foreach (var ra in extracted.RawAmounts)
                    if (decimal.TryParse(ra, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                        invoiceAmount += p;
            }
            if (invoiceAmount > 0m)
                _logger.LogInformation("Using fallback invoice amount: {Amount}", invoiceAmount);
        }

        _logger.LogInformation("Created invoice ID: {Id}, amount: {Amount}", invoiceId, invoiceAmount);

        // Step 6: Send invoice if prompt says "send"
        if (extracted.Action == "send" || extracted.Action == "create_and_send"
            || (extracted.Entities.GetValueOrDefault("invoice")?.ContainsKey("send") ?? false))
        {
            await SendInvoice(api, invoiceId);
        }

        return (invoiceId, invoiceAmount);
    }

    private async Task CreateProjectAndRegisterHours(TripletexApiClient api, ExtractionResult extracted,
        long customerId, Dictionary<string, object> projectEntity, Dictionary<string, object> employeeEntity)
    {
        try
        {
            // 1. Create project
            var projectName = GetStringField(projectEntity, "name") ?? "Prosjekt";
            var projectBody = new Dictionary<string, object>
            {
                ["name"] = projectName,
                ["startDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
                ["customer"] = new { id = customerId }
            };

            // Resolve project manager — need at least one employee
            var firstEmpResult = await api.GetAsync("/employee", new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" });
            if (firstEmpResult.TryGetProperty("values", out var empVals) && empVals.GetArrayLength() > 0)
                projectBody["projectManager"] = new { id = empVals[0].GetProperty("id").GetInt64() };

            var projectResult = await api.PostAsync("/project", projectBody);
            var projectId = projectResult.GetProperty("value").GetProperty("id").GetInt64();
            _logger.LogInformation("Created project '{Name}' ID: {Id}", projectName, projectId);

            // 2. Create or find activity
            var activityName = GetStringField(projectEntity, "activity") ?? "Aktivitet";
            long activityId;

            // Search for existing activity by name
            var activitySearch = await api.GetAsync("/activity", new Dictionary<string, string>
            {
                ["name"] = activityName,
                ["count"] = "1",
                ["fields"] = "id,name,activityType"
            });

            if (activitySearch.TryGetProperty("values", out var actVals) && actVals.GetArrayLength() > 0)
            {
                activityId = actVals[0].GetProperty("id").GetInt64();
                _logger.LogInformation("Found existing activity '{Name}' ID: {Id}", activityName, activityId);
            }
            else
            {
                // Create a new project-general activity
                var activityBody = new Dictionary<string, object>
                {
                    ["name"] = activityName,
                    ["activityType"] = "PROJECT_GENERAL_ACTIVITY"
                };

                // Set rate if hourly rate is specified
                var hourlyRate = GetStringField(projectEntity, "hourlyRate")
                    ?? GetStringField(projectEntity, "rate");
                if (hourlyRate != null && decimal.TryParse(hourlyRate, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                    activityBody["rate"] = rate;

                var activityResult = await api.PostAsync("/activity", activityBody);
                activityId = activityResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created activity '{Name}' ID: {Id}", activityName, activityId);
            }

            // 3. Link activity to project via projectActivity
            var projectActivityBody = new Dictionary<string, object>
            {
                ["project"] = new { id = projectId },
                ["activity"] = new { id = activityId }
            };

            // Set hourly rate on project activity if known
            var hrStr = GetStringField(projectEntity, "hourlyRate")
                ?? GetStringField(projectEntity, "rate");
            if (hrStr != null && decimal.TryParse(hrStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var hrRate))
                projectActivityBody["budgetHourlyRateCurrency"] = hrRate;

            try
            {
                await api.PostAsync("/project/projectActivity", projectActivityBody);
                _logger.LogInformation("Linked activity {ActId} to project {ProjId}", activityId, projectId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to link activity to project (non-fatal)");
            }

            // 4. Find or create employee
            var empFirstName = GetStringField(employeeEntity, "firstName") ?? "";
            var empLastName = GetStringField(employeeEntity, "lastName") ?? "";
            var empEmail = GetStringField(employeeEntity, "email");
            long employeeId;

            var empSearch = new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" };
            if (!string.IsNullOrEmpty(empFirstName)) empSearch["firstName"] = empFirstName;
            if (!string.IsNullOrEmpty(empLastName)) empSearch["lastName"] = empLastName;

            var empResult = await api.GetAsync("/employee", empSearch);
            if (empResult.TryGetProperty("values", out var foundEmps) && foundEmps.GetArrayLength() > 0)
            {
                employeeId = foundEmps[0].GetProperty("id").GetInt64();
                _logger.LogInformation("Found employee {First} {Last} ID: {Id}", empFirstName, empLastName, employeeId);
            }
            else
            {
                // Create employee
                var empBody = new Dictionary<string, object>
                {
                    ["firstName"] = empFirstName,
                    ["lastName"] = !string.IsNullOrEmpty(empLastName) ? empLastName : "Ansatt",
                    ["dateOfBirth"] = "1990-01-01",
                    ["userType"] = "STANDARD"
                };
                if (!string.IsNullOrEmpty(empEmail)) empBody["email"] = empEmail;

                // Need department
                var deptResult = await api.GetAsync("/department", new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id" });
                if (deptResult.TryGetProperty("values", out var dv) && dv.GetArrayLength() > 0)
                    empBody["department"] = new { id = dv[0].GetProperty("id").GetInt64() };

                var createEmpResult = await api.PostAsync("/employee", empBody);
                employeeId = createEmpResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created employee {First} {Last} ID: {Id}", empFirstName, empLastName, employeeId);
            }

            // 5. Register timesheet entry
            decimal hours = 0;
            // Try hours from project entity (may be merged from timeRegistration)
            var hoursStr = GetStringField(projectEntity, "hours") ?? GetStringField(employeeEntity, "hours");
            if (hoursStr != null) decimal.TryParse(hoursStr, NumberStyles.Any, CultureInfo.InvariantCulture, out hours);

            // Fallback: try from invoice orderLines count
            if (hours <= 0)
            {
                var invoiceEntity = extracted.Entities.GetValueOrDefault("invoice") ?? new();
                if (invoiceEntity.TryGetValue("orderLines", out var olVal) && olVal is JsonElement olJson && olJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in olJson.EnumerateArray())
                    {
                        if (item.TryGetProperty("count", out var cnt))
                            hours = cnt.ValueKind == JsonValueKind.Number ? cnt.GetDecimal() : decimal.TryParse(cnt.GetString(), out var h) ? h : 0;
                    }
                }
            }
            if (hours <= 0) hours = 1; // fallback

            var timesheetBody = new Dictionary<string, object>
            {
                ["employee"] = new { id = employeeId },
                ["activity"] = new { id = activityId },
                ["project"] = new { id = projectId },
                ["date"] = DateTime.Today.ToString("yyyy-MM-dd"),
                ["hours"] = hours
            };

            var tsResult = await api.PostAsync("/timesheet/entry", timesheetBody);
            var tsId = tsResult.GetProperty("value").GetProperty("id").GetInt64();
            _logger.LogInformation("Created timesheet entry ID: {Id}, {Hours}h for employee {EmpId} on project {ProjId}",
                tsId, hours, employeeId, projectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create project/timesheet (non-fatal, invoice will still be created)");
        }
    }

    private async Task<long> ResolveOrCreateCustomer(TripletexApiClient api,
        Dictionary<string, object> cust, Dictionary<string, object> invoice, ExtractionResult extracted)
    {
        // Try to find customer name and org number from all available sources
        var custName = GetStringField(cust, "name")
            ?? GetStringField(invoice, "customer")
            ?? GetStringField(invoice, "customerName")
            ?? extracted.Relationships.GetValueOrDefault("customer");

        var orgNumber = GetStringField(cust, "organizationNumber")
            ?? GetStringField(cust, "orgNumber")
            ?? GetStringField(invoice, "customerOrgNumber")
            ?? GetStringField(invoice, "organizationNumber");

        // Single search: prefer org number, fallback to name
        var searchParams = new Dictionary<string, string> { ["count"] = "1", ["fields"] = "id,name" };
        if (!string.IsNullOrEmpty(orgNumber))
            searchParams["organizationNumber"] = orgNumber;
        else if (!string.IsNullOrEmpty(custName))
            searchParams["name"] = custName;

        if (searchParams.Count > 2) // has a search criterion beyond count/fields
        {
            var result = await api.GetAsync("/customer", searchParams);
            if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
                return vals[0].GetProperty("id").GetInt64();
        }

        // Not found — create
        var custBody = new Dictionary<string, object> { ["isCustomer"] = true };
        if (!string.IsNullOrEmpty(custName)) custBody["name"] = custName;
        else custBody["name"] = "Kunde";
        if (!string.IsNullOrEmpty(orgNumber)) custBody["organizationNumber"] = orgNumber;
        SetIfPresent(custBody, cust, "email");

        var custResult = await api.PostAsync("/customer", custBody);
        return custResult.GetProperty("value").GetProperty("id").GetInt64();
    }

    private async Task SendInvoice(TripletexApiClient api, long invoiceId)
    {
        _logger.LogInformation("Sending invoice {Id}", invoiceId);
        try
        {
            await api.PutAsync($"/invoice/{invoiceId}/:send", body: null,
                queryParams: new Dictionary<string, string> { ["sendType"] = "EMAIL" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send invoice {Id}, trying MANUAL", invoiceId);
            try
            {
                await api.PutAsync($"/invoice/{invoiceId}/:send", body: null,
                    queryParams: new Dictionary<string, string> { ["sendType"] = "MANUAL" });
            }
            catch (Exception ex2)
            {
                _logger.LogWarning(ex2, "Failed to send invoice {Id} with MANUAL too", invoiceId);
            }
        }
    }

    internal static List<Dictionary<string, object>> BuildOrderLines(ExtractionResult extracted, long vatTypeId, Dictionary<int, long>? outputRateMap = null)
    {
        var lines = new List<Dictionary<string, object>>();

        // Determine if the stated amounts include VAT.
        // vatIncluded=true  → amounts include VAT, use unitPriceIncludingVatCurrency
        // vatIncluded=false → amounts exclude VAT, use unitPriceExcludingVatCurrency (default)
        // NOTE: vatIncluded=false does NOT mean "no VAT" — it means the stated amount is ex-VAT.
        // VAT is always applied at the standard rate (25%) unless a specific line has vatRate=0.
        bool useIncludingVatPrice = false;
        var invoiceForVat = extracted.Entities.GetValueOrDefault("invoice");
        if (invoiceForVat != null && invoiceForVat.TryGetValue("vatIncluded", out var vatInclObj))
        {
            useIncludingVatPrice = vatInclObj switch
            {
                bool b => b,
                JsonElement je when je.ValueKind == JsonValueKind.True => true,
                _ => false
            };
        }

        // Code-level safeguard: only trust vatIncluded=true if the prompt actually contains
        // VAT-inclusive keywords. The LLM sometimes hallucinate vatIncluded=true.
        if (useIncludingVatPrice)
        {
            var prompt = (extracted.RawPrompt ?? "").ToLowerInvariant();
            var vatInclKeywords = new[] {
                "inkl. mva", "inkl mva", "inklusive mva", "inkludert mva", "inkl.mva",
                "including vat", "incl. vat", "incl vat", "vat included", "vat-inclusive",
                "con iva incluido", "iva incluido", "iva inclusa", "iva incluso",
                "inkl. mwst", "inkl mwst", "inklusive mwst", "einschließlich mwst",
                "ttc", "tva incluse", "tva comprise",
                "inkl. moms", "inkl moms", "inklusive moms",
                "com iva incluído", "com iva"
            };
            if (!vatInclKeywords.Any(k => prompt.Contains(k)))
                useIncludingVatPrice = false;
        }

        // Try to parse from orderLines entity
        var orderLinesEntity = extracted.Entities.GetValueOrDefault("orderLines");
        if (orderLinesEntity is { Count: > 0 })
        {
            foreach (var (key, val) in orderLinesEntity)
            {
                // Each entry might be a line item serialized as JsonElement
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    var line = ParseOrderLineFromJson(je, vatTypeId, outputRateMap, useIncludingVatPrice);
                    lines.Add(line);
                }
            }
        }

        // Also check for orderLines inside the invoice entity (LLM often puts them there)
        if (lines.Count == 0)
        {
            var invoice = extracted.Entities.GetValueOrDefault("invoice");
            if (invoice != null && invoice.TryGetValue("orderLines", out var olVal) && olVal is JsonElement olJson)
            {
                if (olJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in olJson.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var line = ParseOrderLineFromJson(item, vatTypeId, outputRateMap, useIncludingVatPrice);
                            lines.Add(line);
                        }
                    }
                }
            }
        }

        // If no lines parsed, try to build from invoice entity fields (description + amount)
        if (lines.Count == 0)
        {
            var invoice = extracted.Entities.GetValueOrDefault("invoice");
            var desc = invoice != null ? (GetStringField(invoice, "description") ?? GetStringField(invoice, "lineDescription")) : null;
            decimal? amt = null;

            // Try amountExcludingVAT from invoice entity
            if (invoice != null)
            {
                var amtStr = GetStringField(invoice, "amountExcludingVAT")
                    ?? GetStringField(invoice, "amount")
                    ?? GetStringField(invoice, "unitPrice");
                if (amtStr != null && decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    amt = parsed;
            }

            // Fall back to raw_amounts
            if (amt == null && extracted.RawAmounts.Count > 0)
            {
                if (decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var rawAmt))
                    amt = rawAmt;
            }

            if (amt != null)
            {
                var priceField = useIncludingVatPrice ? "unitPriceIncludingVatCurrency" : "unitPriceExcludingVatCurrency";
                lines.Add(new Dictionary<string, object>
                {
                    ["description"] = desc ?? "Vare",
                    ["count"] = 1,
                    [priceField] = amt.Value,
                    ["vatType"] = new { id = vatTypeId }
                });
            }
        }

        // Fallback: at least one line
        if (lines.Count == 0)
        {
            lines.Add(new Dictionary<string, object>
            {
                ["description"] = "Vare",
                ["count"] = 1,
                ["unitPriceExcludingVatCurrency"] = 1000.0,
                ["vatType"] = new { id = vatTypeId }
            });
        }

        return lines;
    }

    private static Dictionary<string, object> ParseOrderLineFromJson(JsonElement je, long defaultVatTypeId, Dictionary<int, long>? outputRateMap = null, bool useIncludingVatPrice = false)
    {
        // Match per-line VAT rate to the correct output VAT type
        long lineVatTypeId = defaultVatTypeId;
        if (je.TryGetProperty("vatRate", out var vatRate))
        {
            int rate = vatRate.ValueKind == JsonValueKind.Number ? (int)vatRate.GetDecimal()
                : int.TryParse(vatRate.GetString(), out var r) ? r : -1;
            if (rate >= 0 && outputRateMap != null && outputRateMap.TryGetValue(rate, out var mappedId))
                lineVatTypeId = mappedId;
        }
        var line = new Dictionary<string, object> { ["vatType"] = new { id = lineVatTypeId } };
        if (je.TryGetProperty("description", out var desc))
            line["description"] = desc.GetString()!;
        // Always default count to 1 — Tripletex defaults to 0 if omitted, making amount=0
        double countVal = 1.0;
        if (je.TryGetProperty("count", out var cnt))
            countVal = cnt.ValueKind == JsonValueKind.Number ? cnt.GetDouble()
                : double.TryParse(cnt.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var cd) ? cd : 1.0;
        line["count"] = countVal;

        // Determine which price field to use based on vatIncluded flag
        var priceField = useIncludingVatPrice ? "unitPriceIncludingVatCurrency" : "unitPriceExcludingVatCurrency";
        if (je.TryGetProperty("unitPrice", out var price))
            line[priceField] = price.ValueKind == JsonValueKind.Number ? price.GetDouble()
                : double.TryParse(price.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pd) ? pd : 0.0;
        if (je.TryGetProperty("unitPriceExcludingVatCurrency", out var price2))
            line["unitPriceExcludingVatCurrency"] = price2.ValueKind == JsonValueKind.Number ? price2.GetDouble()
                : double.TryParse(price2.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pd2) ? pd2 : 0.0;
        return line;
    }

    private async Task EnsureBankAccount(TripletexApiClient api)
    {
        // Fresh Tripletex accounts have no bank account on ledger account 1920.
        // Invoice creation fails with "Faktura kan ikke opprettes før selskapet har registrert et bankkontonummer".
        // Fix: find account 1920, check if bankAccountNumber is set, and set a dummy one if not.
        var result = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["number"] = "1920",
            ["count"] = "1",
            ["fields"] = "id,version,bankAccountNumber,isBankAccount"
        });

        if (result.TryGetProperty("values", out var accounts) && accounts.GetArrayLength() > 0)
        {
            var account = accounts[0];
            var hasBankNumber = account.TryGetProperty("bankAccountNumber", out var bn)
                && bn.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(bn.GetString());

            if (!hasBankNumber)
            {
                var accountId = account.GetProperty("id").GetInt64();
                var version = account.GetProperty("version").GetInt32();
                _logger.LogInformation("Account 1920 (ID {Id}) has no bank account number, setting one", accountId);

                await api.PutAsync($"/ledger/account/{accountId}", new Dictionary<string, object>
                {
                    ["id"] = accountId,
                    ["version"] = version,
                    ["bankAccountNumber"] = "86011117947",
                    ["isBankAccount"] = true
                });
            }
        }
    }

    internal async Task<(long defaultId, JsonElement vatTypes)> ResolveVatTypes(TripletexApiClient api)
    {
        var vatResult = await api.GetAsync("/ledger/vatType", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,number,percentage"
        });

        long defaultId = 1;
        JsonElement vatTypesArray = default;

        if (vatResult.TryGetProperty("values", out vatTypesArray))
        {
            // Prefer output 25% MVA (number "3")
            foreach (var vt in vatTypesArray.EnumerateArray())
            {
                if (vt.TryGetProperty("number", out var num) && num.GetString() == "3")
                {
                    defaultId = vt.GetProperty("id").GetInt64();
                    return (defaultId, vatTypesArray);
                }
            }
            // Fallback: first with percentage > 0
            foreach (var vt in vatTypesArray.EnumerateArray())
            {
                if (vt.TryGetProperty("percentage", out var pct) && pct.GetDecimal() > 0)
                {
                    defaultId = vt.GetProperty("id").GetInt64();
                    return (defaultId, vatTypesArray);
                }
            }
            // Last resort: first one
            foreach (var vt in vatTypesArray.EnumerateArray())
            {
                defaultId = vt.GetProperty("id").GetInt64();
                break;
            }
        }

        return (defaultId, vatTypesArray);
    }

    /// <summary>Build a map of percentage → output VAT type ID, using only sales/output VAT codes.</summary>
    private static Dictionary<int, long> BuildOutputRateMap(JsonElement vatTypes)
    {
        // Output (sales) VAT type numbers in Norwegian chart of accounts
        // "3"=25%, "31"=15%, "32"=12%, "33"=11.11%, "5"=0% utland, "6"=0% uttak
        // Also include any VAT type with percentage=0 for VAT-exempt invoices
        var outputNumbers = new HashSet<string> { "3", "31", "32", "33", "5", "6" };
        var map = new Dictionary<int, long>();

        if (vatTypes.ValueKind != JsonValueKind.Array) return map;

        foreach (var vt in vatTypes.EnumerateArray())
        {
            if (!vt.TryGetProperty("percentage", out var pct)) continue;
            int rate = (int)pct.GetDecimal();

            // Always include if it's in our known output numbers
            if (vt.TryGetProperty("number", out var num))
            {
                var numStr = num.ValueKind == JsonValueKind.String ? num.GetString() : num.GetRawText();
                if (numStr != null && outputNumbers.Contains(numStr))
                {
                    if (!map.ContainsKey(rate))
                        map[rate] = vt.GetProperty("id").GetInt64();
                    continue;
                }
            }

            // Also capture any 0% VAT type (for VAT-exempt invoices)
            if (rate == 0 && !map.ContainsKey(0))
                map[0] = vt.GetProperty("id").GetInt64();
        }

        return map;
    }

    private async Task CreateProductsForLines(TripletexApiClient api, List<Dictionary<string, object>> lines,
        ExtractionResult extracted, long defaultVatTypeId, Dictionary<int, long>? outputRateMap)
    {
        var invoiceEntity = extracted.Entities.GetValueOrDefault("invoice") ?? new();
        if (!invoiceEntity.TryGetValue("orderLines", out var olVal) || olVal is not JsonElement olJson
            || olJson.ValueKind != JsonValueKind.Array)
            return;

        var olArray = olJson.EnumerateArray().ToArray();
        for (int i = 0; i < Math.Min(olArray.Length, lines.Count); i++)
        {
            var rawLine = olArray[i];
            string? productCode = null;
            if (rawLine.TryGetProperty("productCode", out var pc))
                productCode = pc.ValueKind == JsonValueKind.String ? pc.GetString() : pc.GetRawText();
            else if (rawLine.TryGetProperty("productNumber", out var pn))
                productCode = pn.ValueKind == JsonValueKind.String ? pn.GetString() : pn.GetRawText();

            if (string.IsNullOrEmpty(productCode)) continue;

            // Search for existing product by number first (avoids 422 errors in competition)
            long? productId = null;
            var searchResult = await api.GetAsync("/product", new Dictionary<string, string>
            {
                ["number"] = productCode,
                ["count"] = "1",
                ["fields"] = "id"
            });
            if (searchResult.TryGetProperty("values", out var prodVals) && prodVals.GetArrayLength() > 0)
            {
                productId = prodVals[0].GetProperty("id").GetInt64();
                _logger.LogInformation("Found existing product #{Code} ID: {Id}", productCode, productId);
            }
            else
            {
                // Product doesn't exist — create it
                var lineVatType = lines[i].GetValueOrDefault("vatType");
                var productBody = new Dictionary<string, object>
                {
                    ["name"] = lines[i].GetValueOrDefault("description")?.ToString() ?? "Produkt"
                };
                if (int.TryParse(productCode, out var pNum))
                    productBody["number"] = pNum;
                if (lineVatType != null)
                    productBody["vatType"] = lineVatType;
                if (lines[i].TryGetValue("unitPriceExcludingVatCurrency", out var price))
                    productBody["priceExcludingVatCurrency"] = price;

                try
                {
                    var prodResult = await api.PostAsync("/product", productBody);
                    productId = prodResult.GetProperty("value").GetProperty("id").GetInt64();
                    _logger.LogInformation("Created product '{Name}' (#{Code}) ID: {Id}",
                        productBody["name"], productCode, productId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create product {Code} (non-fatal)", productCode);
                }
            }

            if (productId != null)
                lines[i]["product"] = new { id = productId.Value };
        }
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

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
        {
            body[key] = val is JsonElement je ? je.ToString() : val;
        }
    }
}
