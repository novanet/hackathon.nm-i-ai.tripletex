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
        // Full chain: Customer → Order (with OrderLines + VatType) → Invoice
        var cust = extracted.Entities.GetValueOrDefault("customer") ?? new();
        var invoice = extracted.Entities.GetValueOrDefault("invoice") ?? new();
        var orderLines = extracted.Entities.GetValueOrDefault("orderLines");

        // Step 1: Create customer
        var custBody = new Dictionary<string, object> { ["isCustomer"] = true };
        SetIfPresent(custBody, cust, "name");
        SetIfPresent(custBody, cust, "email");
        SetIfPresent(custBody, cust, "organizationNumber");

        // Use a default name if none extracted
        if (!custBody.ContainsKey("name"))
            custBody["name"] = "Kunde";

        var custResult = await api.PostAsync("/customer", custBody);
        var customerId = custResult.GetProperty("value").GetProperty("id").GetInt32();
        _logger.LogInformation("Created customer ID: {Id}", customerId);

        // Step 2: Resolve VAT type (need at least one for order lines)
        var vatTypeId = await ResolveDefaultVatTypeId(api);

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
        var orderId = orderResult.GetProperty("value").GetProperty("id").GetInt32();
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
        var invoiceId = invoiceResult.GetProperty("value").GetProperty("id").GetInt32();
        _logger.LogInformation("Created invoice ID: {Id}", invoiceId);
    }

    internal static List<Dictionary<string, object>> BuildOrderLines(ExtractionResult extracted, int vatTypeId)
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
                    var line = new Dictionary<string, object> { ["vatType"] = new { id = vatTypeId } };
                    if (je.TryGetProperty("description", out var desc))
                        line["description"] = desc.GetString()!;
                    if (je.TryGetProperty("count", out var cnt))
                        line["count"] = cnt.GetDouble();
                    if (je.TryGetProperty("unitPrice", out var price))
                        line["unitPriceExcludingVatCurrency"] = price.GetDouble();
                    if (je.TryGetProperty("unitPriceExcludingVatCurrency", out var price2))
                        line["unitPriceExcludingVatCurrency"] = price2.GetDouble();
                    lines.Add(line);
                }
            }
        }

        // If no lines parsed, try to build from raw amounts
        if (lines.Count == 0 && extracted.RawAmounts.Count > 0)
        {
            if (decimal.TryParse(extracted.RawAmounts[0], out var amount))
            {
                lines.Add(new Dictionary<string, object>
                {
                    ["description"] = "Vare",
                    ["count"] = 1,
                    ["unitPriceExcludingVatCurrency"] = amount,
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

    internal async Task<int> ResolveDefaultVatTypeId(TripletexApiClient api)
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
                    return vt.GetProperty("id").GetInt32();
            }
            // Fallback: first with percentage > 0
            foreach (var vt in vatTypes.EnumerateArray())
            {
                if (vt.TryGetProperty("percentage", out var pct) && pct.GetDecimal() > 0)
                    return vt.GetProperty("id").GetInt32();
            }
            // Last resort: first one
            foreach (var vt in vatTypes.EnumerateArray())
                return vt.GetProperty("id").GetInt32();
        }

        return 1; // absolute fallback
    }

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
            return val is JsonElement je ? je.GetString() : val.ToString();
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
