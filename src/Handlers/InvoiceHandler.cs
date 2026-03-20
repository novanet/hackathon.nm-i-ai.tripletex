using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class InvoiceHandler : ITaskHandler
{
    private readonly ILogger<InvoiceHandler> _logger;

    public InvoiceHandler(ILogger<InvoiceHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        await CreateInvoiceChainAsync(api, extracted);
    }

    public async Task<(long invoiceId, decimal amount)> CreateInvoiceChainAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Full chain: Customer (find or create) → Order (with OrderLines + VatType) → Invoice → optionally Send
        var cust = extracted.Entities.GetValueOrDefault("customer") ?? new();
        var invoice = extracted.Entities.GetValueOrDefault("invoice") ?? new();

        // Step 1+2: Find/create customer AND resolve VAT type in parallel
        var customerTask = ResolveOrCreateCustomer(api, cust, invoice, extracted);
        var vatTypeTask = ResolveDefaultVatTypeId(api);
        await Task.WhenAll(customerTask, vatTypeTask);
        var customerId = customerTask.Result;
        var vatTypeId = vatTypeTask.Result;
        _logger.LogInformation("Using customer ID: {Id}", customerId);

        // Step 3: Build order lines
        var lines = BuildOrderLines(extracted, vatTypeId);

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
        var invoiceAmount = invoiceValue.TryGetProperty("amount", out var amtProp) ? amtProp.GetDecimal() : 0m;
        _logger.LogInformation("Created invoice ID: {Id}", invoiceId);

        // Step 6: Send invoice if prompt says "send"
        if (extracted.Action == "send" || extracted.Action == "create_and_send"
            || (extracted.Entities.GetValueOrDefault("invoice")?.ContainsKey("send") ?? false))
        {
            await SendInvoice(api, invoiceId);
        }

        return (invoiceId, invoiceAmount);
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

    internal static List<Dictionary<string, object>> BuildOrderLines(ExtractionResult extracted, long vatTypeId)
    {
        var lines = new List<Dictionary<string, object>>();

        // Try to parse from orderLines entity
        var orderLinesEntity = extracted.Entities.GetValueOrDefault("orderLines");
        if (orderLinesEntity is { Count: > 0 })
        {
            foreach (var (key, val) in orderLinesEntity)
            {
                // Each entry might be a line item serialized as JsonElement
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    var line = ParseOrderLineFromJson(je, vatTypeId);
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
                            var line = ParseOrderLineFromJson(item, vatTypeId);
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
                lines.Add(new Dictionary<string, object>
                {
                    ["description"] = desc ?? "Vare",
                    ["count"] = 1,
                    ["unitPriceExcludingVatCurrency"] = amt.Value,
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

    private static Dictionary<string, object> ParseOrderLineFromJson(JsonElement je, long vatTypeId)
    {
        var line = new Dictionary<string, object> { ["vatType"] = new { id = vatTypeId } };
        if (je.TryGetProperty("description", out var desc))
            line["description"] = desc.GetString()!;
        if (je.TryGetProperty("count", out var cnt))
            line["count"] = cnt.ValueKind == JsonValueKind.Number ? cnt.GetDouble()
                : double.TryParse(cnt.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var cd) ? cd : 1.0;
        if (je.TryGetProperty("unitPrice", out var price))
            line["unitPriceExcludingVatCurrency"] = price.ValueKind == JsonValueKind.Number ? price.GetDouble()
                : double.TryParse(price.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pd) ? pd : 0.0;
        if (je.TryGetProperty("unitPriceExcludingVatCurrency", out var price2))
            line["unitPriceExcludingVatCurrency"] = price2.ValueKind == JsonValueKind.Number ? price2.GetDouble()
                : double.TryParse(price2.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pd2) ? pd2 : 0.0;
        return line;
    }

    internal async Task<long> ResolveDefaultVatTypeId(TripletexApiClient api)
    {
        var vatResult = await api.GetAsync("/ledger/vatType", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,number,percentage"
        });

        if (vatResult.TryGetProperty("values", out var vatTypes))
        {
            // Prefer 25% MVA (number "3")
            foreach (var vt in vatTypes.EnumerateArray())
            {
                if (vt.TryGetProperty("number", out var num) && num.GetString() == "3")
                    return vt.GetProperty("id").GetInt64();
            }
            // Fallback: first with percentage > 0
            foreach (var vt in vatTypes.EnumerateArray())
            {
                if (vt.TryGetProperty("percentage", out var pct) && pct.GetDecimal() > 0)
                    return vt.GetProperty("id").GetInt64();
            }
            // Last resort: first one
            foreach (var vt in vatTypes.EnumerateArray())
                return vt.GetProperty("id").GetInt64();
        }

        return 1; // absolute fallback
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
