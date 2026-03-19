using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class CustomerHandler : ITaskHandler
{
    private readonly ILogger<CustomerHandler> _logger;

    public CustomerHandler(ILogger<CustomerHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var cust = extracted.Entities.GetValueOrDefault("customer") ?? new();

        var body = new Dictionary<string, object> { ["isCustomer"] = true };

        SetIfPresent(body, cust, "name");
        SetIfPresent(body, cust, "email");
        SetIfPresent(body, cust, "organizationNumber");
        SetIfPresent(body, cust, "phoneNumber");
        SetIfPresent(body, cust, "phoneNumberMobile");
        SetIfPresent(body, cust, "invoiceEmail");
        SetIfPresent(body, cust, "isPrivateIndividual");

        // Handle address if present
        var address = BuildAddress(cust);
        if (address.Count > 0)
            body["physicalAddress"] = address;

        // Handle postal address if different
        var postalAddress = BuildPostalAddress(cust);
        if (postalAddress.Count > 0)
            body["postalAddress"] = postalAddress;

        _logger.LogInformation("Creating customer: {Name}", body.GetValueOrDefault("name"));

        var result = await api.PostAsync("/customer", body);
        var customerId = result.GetProperty("value").GetProperty("id").GetInt32();

        _logger.LogInformation("Created customer ID: {Id}", customerId);
    }

    private static Dictionary<string, object> BuildAddress(Dictionary<string, object> cust)
    {
        var addr = new Dictionary<string, object>();
        SetIfPresent(addr, cust, "addressLine1");
        SetIfPresent(addr, cust, "postalCode");
        SetIfPresent(addr, cust, "city");
        if (cust.TryGetValue("countryId", out var countryId))
            addr["country"] = new { id = int.Parse(countryId.ToString()!) };
        return addr;
    }

    private static Dictionary<string, object> BuildPostalAddress(Dictionary<string, object> cust)
    {
        var addr = new Dictionary<string, object>();
        SetIfPresent(addr, cust, "postalAddressLine1", "addressLine1");
        SetIfPresent(addr, cust, "postalPostalCode", "postalCode");
        SetIfPresent(addr, cust, "postalCity", "city");
        return addr;
    }

    private static void SetIfPresent(Dictionary<string, object> body, Dictionary<string, object> source, string key, string? targetKey = null)
    {
        if (source.TryGetValue(key, out var val) && val is not null)
        {
            body[targetKey ?? key] = val is JsonElement je ? je.ToString() : val;
        }
    }
}
