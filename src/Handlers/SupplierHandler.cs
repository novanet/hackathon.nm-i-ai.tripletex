using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class SupplierHandler : ITaskHandler
{
    private readonly ILogger<SupplierHandler> _logger;

    public SupplierHandler(ILogger<SupplierHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Collect all supplier entities (support multi-entity: supplier, supplier1, supplier2, ...)
        var supplierEntities = new List<Dictionary<string, object>>();
        foreach (var kvp in extracted.Entities)
        {
            if (kvp.Key == "supplier" || kvp.Key.StartsWith("supplier"))
                supplierEntities.Add(kvp.Value);
        }
        if (supplierEntities.Count == 0)
            supplierEntities.Add(new());

        foreach (var sup in supplierEntities)
        {
            await CreateSingleSupplier(api, sup);
        }
    }

    private async Task CreateSingleSupplier(TripletexApiClient api, Dictionary<string, object> sup)
    {
        var body = new Dictionary<string, object>();
        SetIfPresent(body, sup, "name");
        SetIfPresent(body, sup, "email");
        SetIfPresent(body, sup, "organizationNumber");
        SetIfPresent(body, sup, "supplierNumber");
        SetIfPresent(body, sup, "phoneNumber");
        SetIfPresent(body, sup, "phoneNumberMobile");

        // Handle address if present
        var address = new Dictionary<string, object>();
        SetIfPresent(address, sup, "addressLine1");
        SetIfPresent(address, sup, "postalCode");
        SetIfPresent(address, sup, "city");
        if (address.Count > 0)
            body["physicalAddress"] = address;

        _logger.LogInformation("Creating supplier: {Name}", body.GetValueOrDefault("name"));

        var result = await api.PostAsync("/supplier", body);
        var supplierId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created supplier ID: {Id}", supplierId);
    }
    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
        {
            body[key] = val is JsonElement je ? je.ToString() : val;
        }
    }
}
