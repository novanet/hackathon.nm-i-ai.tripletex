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
        // Detect which variant of register_payment this is:
        // 1. Reversal: action=reverse → find existing paid invoice, reverse payment
        // 2. Full chain: has orderLines → create customer→order→invoice→pay (existing path)
        // 3. Simple pay: no orderLines → find existing unpaid invoice, register payment

        if (extracted.Action == "reverse")
        {
            _logger.LogInformation("Payment variant: REVERSAL");
            return await HandleReversalAsync(api, extracted);
        }

        var hasOrderLines = HasOrderLines(extracted);
        if (hasOrderLines)
        {
            _logger.LogInformation("Payment variant: FULL CHAIN (has order lines)");
            return await HandleFullChainPaymentAsync(api, extracted);
        }

        // Simple pay: try to find existing invoice first, fall back to full chain
        _logger.LogInformation("Payment variant: SIMPLE PAY (find existing invoice)");
        return await HandleSimplePayAsync(api, extracted);
    }

    /// <summary>Full chain: Customer → Order → Invoice → Payment (original path)</summary>
    private async Task<HandlerResult> HandleFullChainPaymentAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var paymentTypeTask = ResolvePaymentTypeId(api);
        var (invoiceId, _) = await _invoiceHandler.CreateInvoiceChainAsync(api, extracted);
        var paymentTypeId = await paymentTypeTask;

        // Always use amountOutstanding from GET — it's the VAT-inclusive balance
        var paidAmount = await GetAmountOutstanding(api, invoiceId);

        var paymentDate = ResolvePaymentDate(extracted);
        await RegisterPayment(api, invoiceId, paymentTypeId, paidAmount, paymentDate);
        return PaymentResult(invoiceId, paidAmount);
    }

    /// <summary>Simple pay: find existing unpaid invoice by customer, pay its amountOutstanding</summary>
    private async Task<HandlerResult> HandleSimplePayAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var customerId = await FindCustomerId(api, extracted);
        if (customerId.HasValue)
        {
            // Search for unpaid invoices for this customer
            var invoices = await api.GetAsync("/invoice", new Dictionary<string, string>
            {
                ["customerId"] = customerId.Value.ToString(),
                ["from"] = "0",
                ["count"] = "20",
                ["fields"] = "id,amount,amountOutstanding,amountCurrency"
            });

            if (invoices.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            {
                // Find invoice with amountOutstanding > 0 (unpaid)
                foreach (var inv in vals.EnumerateArray())
                {
                    var outstanding = inv.TryGetProperty("amountOutstanding", out var ao)
                        && ao.ValueKind == JsonValueKind.Number ? ao.GetDecimal() : 0m;
                    if (outstanding > 0)
                    {
                        var invoiceId = inv.GetProperty("id").GetInt64();
                        _logger.LogInformation("Found existing unpaid invoice {Id} with amountOutstanding={Outstanding}", invoiceId, outstanding);

                        var paymentTypeId = await ResolvePaymentTypeId(api);
                        var paymentDate = ResolvePaymentDate(extracted);
                        await RegisterPayment(api, invoiceId, paymentTypeId, outstanding, paymentDate);
                        return PaymentResult(invoiceId, outstanding);
                    }
                }
                _logger.LogWarning("Customer {Id} has invoices but none with amountOutstanding > 0, falling back to full chain", customerId);
            }
            else
            {
                _logger.LogWarning("No invoices found for customer {Id}, falling back to full chain", customerId);
            }
        }
        else
        {
            _logger.LogWarning("Could not find existing customer, falling back to full chain");
        }

        // Fallback: create the full chain
        return await HandleFullChainPaymentAsync(api, extracted);
    }

    /// <summary>Reversal: find existing paid invoice, reverse payment via credit note</summary>
    private async Task<HandlerResult> HandleReversalAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var customerId = await FindCustomerId(api, extracted);
        if (customerId.HasValue)
        {
            var invoices = await api.GetAsync("/invoice", new Dictionary<string, string>
            {
                ["customerId"] = customerId.Value.ToString(),
                ["from"] = "0",
                ["count"] = "20",
                ["fields"] = "id,amount,amountOutstanding,amountCurrency"
            });

            if (invoices.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            {
                // Find a paid invoice (amountOutstanding == 0)
                foreach (var inv in vals.EnumerateArray())
                {
                    var outstanding = inv.TryGetProperty("amountOutstanding", out var ao)
                        && ao.ValueKind == JsonValueKind.Number ? ao.GetDecimal() : -1m;
                    if (outstanding == 0)
                    {
                        var invoiceId = inv.GetProperty("id").GetInt64();
                        var amount = inv.TryGetProperty("amount", out var amtProp)
                            && amtProp.ValueKind == JsonValueKind.Number ? amtProp.GetDecimal() : 0m;
                        _logger.LogInformation("Found paid invoice {Id} (amount={Amount}), reversing payment", invoiceId, amount);

                        // Try credit note first (most reliable reversal method)
                        try
                        {
                            var creditDate = ResolvePaymentDate(extracted);
                            await api.PutAsync($"/invoice/{invoiceId}/:createCreditNote",
                                body: null,
                                queryParams: new Dictionary<string, string>
                                {
                                    ["date"] = creditDate,
                                    ["comment"] = "Payment reversal"
                                });
                            _logger.LogInformation("Created credit note for invoice {Id}", invoiceId);
                            return new HandlerResult
                            {
                                EntityType = "invoice",
                                EntityId = invoiceId,
                                Metadata = { ["paymentReversed"] = "true", ["method"] = "creditNote" }
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Credit note failed for invoice {Id}, trying negative payment", invoiceId);
                        }

                        // Fallback: negative payment
                        try
                        {
                            var paymentTypeId = await ResolvePaymentTypeId(api);
                            var paymentDate = ResolvePaymentDate(extracted);
                            await RegisterPayment(api, invoiceId, paymentTypeId, -amount, paymentDate);
                            _logger.LogInformation("Registered negative payment of {Amount} on invoice {Id}", -amount, invoiceId);
                            return new HandlerResult
                            {
                                EntityType = "invoice",
                                EntityId = invoiceId,
                                Metadata = { ["paymentReversed"] = "true", ["method"] = "negativePayment" }
                            };
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogWarning(ex2, "Negative payment also failed for invoice {Id}", invoiceId);
                        }
                    }
                }
                _logger.LogWarning("No paid invoices found for customer {Id}", customerId);
            }
        }

        // Last resort fallback: create full chain and pay (at least passes invoice_found check)
        _logger.LogWarning("Reversal failed — falling back to full chain + pay");
        return await HandleFullChainPaymentAsync(api, extracted);
    }

    private async Task<long?> FindCustomerId(TripletexApiClient api, ExtractionResult extracted)
    {
        var cust = extracted.Entities.GetValueOrDefault("customer") ?? new();
        var invoice = extracted.Entities.GetValueOrDefault("invoice") ?? new();

        var orgNumber = GetStringField(cust, "organizationNumber")
            ?? GetStringField(cust, "orgNumber")
            ?? GetStringField(invoice, "customerOrgNumber")
            ?? GetStringField(invoice, "organizationNumber");

        var custName = GetStringField(cust, "name")
            ?? GetStringField(invoice, "customer")
            ?? GetStringField(invoice, "customerName")
            ?? extracted.Relationships.GetValueOrDefault("customer");

        // Search by org number first (most reliable)
        if (!string.IsNullOrEmpty(orgNumber))
        {
            var result = await api.GetAsync("/customer", new Dictionary<string, string>
            {
                ["organizationNumber"] = orgNumber,
                ["count"] = "1",
                ["fields"] = "id,name"
            });
            if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            {
                var id = vals[0].GetProperty("id").GetInt64();
                _logger.LogInformation("Found customer by org number {Org}: ID={Id}", orgNumber, id);
                return id;
            }
        }

        // Search by name
        if (!string.IsNullOrEmpty(custName))
        {
            var result = await api.GetAsync("/customer", new Dictionary<string, string>
            {
                ["name"] = custName,
                ["count"] = "1",
                ["fields"] = "id,name"
            });
            if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
            {
                var id = vals[0].GetProperty("id").GetInt64();
                _logger.LogInformation("Found customer by name '{Name}': ID={Id}", custName, id);
                return id;
            }
        }

        return null;
    }

    private async Task<decimal> GetAmountOutstanding(TripletexApiClient api, long invoiceId)
    {
        var invoiceGet = await api.GetAsync($"/invoice/{invoiceId}",
            new Dictionary<string, string> { ["fields"] = "id,amount,amountOutstanding" });
        var invoiceData = invoiceGet.GetProperty("value");

        // Prefer amountOutstanding — it's the VAT-inclusive remaining balance
        if (invoiceData.TryGetProperty("amountOutstanding", out var aoProp)
            && aoProp.ValueKind == JsonValueKind.Number && aoProp.GetDecimal() > 0)
        {
            var ao = aoProp.GetDecimal();
            _logger.LogInformation("Invoice {Id} amountOutstanding={Outstanding}", invoiceId, ao);
            return ao;
        }

        // Fallback to amount (total inc VAT)
        if (invoiceData.TryGetProperty("amount", out var amtProp)
            && amtProp.ValueKind == JsonValueKind.Number && amtProp.GetDecimal() > 0)
        {
            var amt = amtProp.GetDecimal();
            _logger.LogInformation("Invoice {Id} using amount={Amount} (amountOutstanding unavailable)", invoiceId, amt);
            return amt;
        }

        _logger.LogWarning("Invoice {Id}: could not read amount fields, using 0", invoiceId);
        return 0m;
    }

    private async Task RegisterPayment(TripletexApiClient api, long invoiceId, long paymentTypeId, decimal paidAmount, string paymentDate)
    {
        await api.PutAsync(
            $"/invoice/{invoiceId}/:payment",
            body: null,
            queryParams: new Dictionary<string, string>
            {
                ["paymentDate"] = paymentDate,
                ["paymentTypeId"] = paymentTypeId.ToString(),
                ["paidAmount"] = paidAmount.ToString("F2", CultureInfo.InvariantCulture)
            });
        _logger.LogInformation("Registered payment of {Amount} on invoice {InvoiceId}", paidAmount, invoiceId);
    }

    private string ResolvePaymentDate(ExtractionResult extracted)
    {
        var payment = extracted.Entities.GetValueOrDefault("payment");
        if (payment?.TryGetValue("paymentDate", out var pd) == true)
            return pd is JsonElement jePd ? jePd.GetString()! : pd.ToString()!;
        if (extracted.Dates.Count > 0)
            return extracted.Dates[^1];
        return DateTime.Now.ToString("yyyy-MM-dd");
    }

    private static bool HasOrderLines(ExtractionResult extracted)
    {
        // Check for orderLines entity
        if (extracted.Entities.ContainsKey("orderLines") && extracted.Entities["orderLines"].Count > 0)
            return true;

        // Check for orderLines inside invoice entity
        var invoice = extracted.Entities.GetValueOrDefault("invoice");
        if (invoice != null && invoice.TryGetValue("orderLines", out var olVal))
        {
            if (olVal is JsonElement olJson && olJson.ValueKind == JsonValueKind.Array && olJson.GetArrayLength() > 0)
                return true;
        }

        return false;
    }

    private static HandlerResult PaymentResult(long invoiceId, decimal paidAmount)
    {
        return new HandlerResult
        {
            EntityType = "invoice",
            EntityId = invoiceId,
            Metadata = { ["paymentRegistered"] = "true", ["paidAmount"] = paidAmount.ToString(CultureInfo.InvariantCulture) }
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
}
