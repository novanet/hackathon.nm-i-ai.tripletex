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

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Full chain: Customer → Order → Invoice → Payment

        // Step 1-3: Create the invoice (reuse InvoiceHandler logic)
        await _invoiceHandler.HandleAsync(api, extracted);

        // Find the invoice we just created (it's the latest one)
        var invoiceSearch = await api.GetAsync("/invoice", new Dictionary<string, string>
        {
            ["count"] = "1",
            ["sorting"] = "-id",
            ["fields"] = "id,amount"
        });

        if (!invoiceSearch.TryGetProperty("values", out var invoices) || invoices.GetArrayLength() == 0)
        {
            _logger.LogWarning("No invoice found for payment registration");
            return;
        }

        var invoiceId = invoices[0].GetProperty("id").GetInt32();
        var invoiceAmount = invoices[0].TryGetProperty("amount", out var amt)
            ? amt.GetDecimal() : 0m;

        // Resolve payment type
        var paymentTypeId = await ResolvePaymentTypeId(api);

        // Get payment amount from extracted data or use invoice amount
        var paidAmount = invoiceAmount;
        var payment = extracted.Entities.GetValueOrDefault("payment");
        if (payment is not null && payment.TryGetValue("amount", out var payAmt))
        {
            if (decimal.TryParse(payAmt is JsonElement je ? je.GetString() : payAmt.ToString(), out var parsed))
                paidAmount = parsed;
        }
        else if (extracted.RawAmounts.Count > 0 && decimal.TryParse(extracted.RawAmounts[^1], out var rawAmt))
        {
            paidAmount = rawAmt;
        }

        var paymentDate = DateTime.Now.ToString("yyyy-MM-dd");
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
    }

    private async Task<int> ResolvePaymentTypeId(TripletexApiClient api)
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
                        return t.GetProperty("id").GetInt32();
                }
            }
            // Fallback: first one
            foreach (var t in types.EnumerateArray())
                return t.GetProperty("id").GetInt32();
        }

        return 1;
    }
}
