using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class PaymentHandler : ITaskHandler
{
    private readonly InvoiceHandler _invoiceHandler;
    private readonly ILogger<PaymentHandler> _logger;

    public PaymentHandler(InvoiceHandler invoiceHandler, ILogger<PaymentHandler> logger)
    {
        _invoiceHandler = invoiceHandler;
        _logger = logger;
    }

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Full chain: Customer → Order → Invoice → Payment

        // Start payment type resolution early (runs in parallel with invoice chain)
        var paymentTypeTask = ResolvePaymentTypeId(api);

        // Step 1-3: Create the invoice (reuse InvoiceHandler logic)
        var (invoiceId, invoiceAmount) = await _invoiceHandler.CreateInvoiceChainAsync(api, extracted);

        // Resolve payment type (already started, just await)
        var paymentTypeId = await paymentTypeTask;

        // GET the invoice to read the actual total including VAT
        // The CreateInvoiceChainAsync amount may be ex-VAT (from order line fallback calculation)
        var invoiceGet = await api.GetAsync($"/invoice/{invoiceId}", new Dictionary<string, string> { ["fields"] = "id,amount,amountOutstanding" });
        var invoiceData = invoiceGet.GetProperty("value");
        var paidAmount = invoiceAmount;
        if (invoiceData.TryGetProperty("amount", out var realAmt) && realAmt.ValueKind == JsonValueKind.Number && realAmt.GetDecimal() > 0)
        {
            paidAmount = realAmt.GetDecimal();
            _logger.LogInformation("Invoice actual amount (inc VAT): {Amount} (chain returned: {ChainAmount})", paidAmount, invoiceAmount);
        }

        var paymentDate = DateTime.Now.ToString("yyyy-MM-dd");
        var payment = extracted.Entities.GetValueOrDefault("payment");
        if (payment?.TryGetValue("paymentDate", out var pd) == true)
            paymentDate = pd is JsonElement jePd ? jePd.GetString()! : pd.ToString()!;
        else if (extracted.Dates.Count > 0)
            paymentDate = extracted.Dates[^1];

        // Register payment (PUT with query params, no body)
        await api.PutAsync(
            $"/invoice/{invoiceId}/:payment",
            body: null,
            queryParams: new Dictionary<string, string>
            {
                ["paymentDate"] = paymentDate,
                ["paymentTypeId"] = paymentTypeId.ToString(),
                ["paidAmount"] = paidAmount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            });

        _logger.LogInformation("Registered payment of {Amount} on invoice {InvoiceId}", paidAmount, invoiceId);
        return new HandlerResult
        {
            EntityType = "invoice",
            EntityId = invoiceId,
            Metadata = { ["paymentRegistered"] = "true", ["paidAmount"] = paidAmount.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
    }

    private async Task<long> ResolvePaymentTypeId(TripletexApiClient api)
    {
        var result = await api.GetAsync("/invoice/paymentType", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,description"
        });

        if (result.TryGetProperty("values", out var types))
        {
            // Prefer bank transfer
            foreach (var t in types.EnumerateArray())
            {
                if (t.TryGetProperty("description", out var desc))
                {
                    var d = desc.GetString()?.ToLowerInvariant() ?? "";
                    if (d.Contains("bank") || d.Contains("overf"))
                        return t.GetProperty("id").GetInt64();
                }
            }
            // Fallback: first one
            foreach (var t in types.EnumerateArray())
                return t.GetProperty("id").GetInt64();
        }

        return 1;
    }
}
