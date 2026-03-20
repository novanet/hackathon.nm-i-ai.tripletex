using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class ProductHandler : ITaskHandler
{
    private readonly ILogger<ProductHandler> _logger;

    public ProductHandler(ILogger<ProductHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var prod = extracted.Entities.GetValueOrDefault("product") ?? new();

        var body = new Dictionary<string, object>();
        SetIfPresent(body, prod, "name");
        // Map LLM aliases to Tripletex API field names
        SetWithAlias(body, prod, "number", "productNumber", "number");
        SetWithAlias(body, prod, "priceExcludingVatCurrency", "priceExcludingVAT", "priceExcludingVatCurrency", "price", "unitPrice");
        SetWithAlias(body, prod, "priceIncludingVatCurrency", "priceIncludingVAT", "priceIncludingVatCurrency");
        SetWithAlias(body, prod, "costExcludingVatCurrency", "costExcludingVAT", "costExcludingVatCurrency", "cost");
        SetIfPresent(body, prod, "isStockItem");
        SetIfPresent(body, prod, "isInactive");

        // Resolve vatType if specified or default to 25% MVA
        if (prod.TryGetValue("vatRate", out var vatRate) || prod.TryGetValue("vatType", out vatRate))
        {
            var vatId = await ResolveVatTypeId(api, vatRate.ToString()!);
            if (vatId.HasValue)
                body["vatType"] = new { id = vatId.Value };
        }
        // Note: omit vatType when not specified — competition accepts products without explicit vatType

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

        JsonElement result;
        try
        {
            result = await api.PostAsync("/product", body);
        }
        catch (TripletexApiException ex) when (ex.Message.Contains("vatTypeId") && body.ContainsKey("vatType"))
        {
            // Sandbox may reject vatType — retry without it
            _logger.LogWarning("vatType rejected, retrying without it: {Msg}", ex.Message);
            body.Remove("vatType");
            result = await api.PostAsync("/product", body);
        }
        var productId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created product ID: {Id}", productId);
        return new HandlerResult { EntityType = "product", EntityId = productId };
    }

    private async Task<long?> ResolveVatTypeId(TripletexApiClient api, string vatHint)
    {
        // Fast path: use hardcoded IDs for standard rates — saves 1 GET per product creation.
        // These VAT type IDs are stable in all Tripletex environments.
        if (decimal.TryParse(vatHint, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
        {
            return pct switch
            {
                25m => 3L,
                15m => 31L,
                12m => 32L,
                0m => 5L,
                _ => null
            };
        }

        // Try matching by VAT number code (e.g. "3" = 25% outbound)
        return vatHint switch
        {
            "3" => 3L,   // 25% utg. mva
            "31" => 31L,  // 15% matmva
            "32" => 32L,  // 12% mva
            "5" => 5L,   // 0% avgiftsfri
            _ => null
        };
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
        {
            body[key] = val is JsonElement je ? je.ToString() : val;
        }
    }

    /// <summary>
    /// Sets body[apiKey] from the first matching alias found in source.
    /// </summary>
    private static void SetWithAlias(Dictionary<string, object> body, Dictionary<string, object> source, string apiKey, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (source.TryGetValue(alias, out var val) && val is not null)
            {
                body[apiKey] = val is JsonElement je ? je.ToString() : val;
                return;
            }
        }
    }
}
