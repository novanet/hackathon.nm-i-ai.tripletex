using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class ProductHandler : ITaskHandler
{
    private readonly ILogger<ProductHandler> _logger;

    public ProductHandler(ILogger<ProductHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var prod = extracted.Entities.GetValueOrDefault("product") ?? new();

        var body = new Dictionary<string, object>();
        SetIfPresent(body, prod, "name");
        SetIfPresent(body, prod, "number");
        SetIfPresent(body, prod, "priceExcludingVatCurrency");
        SetIfPresent(body, prod, "priceIncludingVatCurrency");
        SetIfPresent(body, prod, "costExcludingVatCurrency");
        SetIfPresent(body, prod, "isStockItem");
        SetIfPresent(body, prod, "isInactive");

        // Resolve vatType if specified or default to 25% MVA
        if (prod.TryGetValue("vatRate", out var vatRate) || prod.TryGetValue("vatType", out vatRate))
        {
            var vatId = await ResolveVatTypeId(api, vatRate.ToString()!);
            if (vatId.HasValue)
                body["vatType"] = new { id = vatId.Value };
        }
        else
        {
            // Default: try to find 25% MVA
            var vatId = await ResolveVatTypeId(api, "25");
            if (vatId.HasValue)
                body["vatType"] = new { id = vatId.Value };
        }

        // Resolve account if specified
        if (prod.TryGetValue("account", out var accountNum))
        {
            var accResult = await api.GetAsync("/ledger/account", new Dictionary<string, string>
            {
                ["number"] = accountNum.ToString()!,
                ["count"] = "1",
                ["fields"] = "id"
            });
            if (accResult.TryGetProperty("values", out var accs) && accs.GetArrayLength() > 0)
                body["account"] = new { id = accs[0].GetProperty("id").GetInt64() };
        }

        _logger.LogInformation("Creating product: {Name}", body.GetValueOrDefault("name"));

        var result = await api.PostAsync("/product", body);
        var productId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created product ID: {Id}", productId);
    }

    private async Task<long?> ResolveVatTypeId(TripletexApiClient api, string vatHint)
    {
        var vatResult = await api.GetAsync("/ledger/vatType", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,name,number,percentage"
        });

        if (!vatResult.TryGetProperty("values", out var vatTypes))
            return null;

        // Try to match by percentage (e.g. "25" → 25%)
        if (decimal.TryParse(vatHint, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
        {
            foreach (var vt in vatTypes.EnumerateArray())
            {
                if (vt.TryGetProperty("percentage", out var vtPct) && vtPct.GetDecimal() == pct)
                    return vt.GetProperty("id").GetInt64();
            }
        }

        // Try to match by number
        foreach (var vt in vatTypes.EnumerateArray())
        {
            if (vt.TryGetProperty("number", out var num) && num.GetString() == vatHint)
                return vt.GetProperty("id").GetInt64();
        }

        // Return first non-zero if available
        foreach (var vt in vatTypes.EnumerateArray())
        {
            if (vt.TryGetProperty("percentage", out var vtPct) && vtPct.GetDecimal() > 0)
                return vt.GetProperty("id").GetInt64();
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
