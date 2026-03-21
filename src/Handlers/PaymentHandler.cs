using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class PaymentHandler : ITaskHandler
{
    private readonly InvoiceHandler _invoiceHandler;
    private readonly ILogger<PaymentHandler> _logger;

    // Cache payment type IDs per session hash (constant per clean environment)
    private static readonly Dictionary<int, long> _paymentTypeCache = new();
    private static readonly object _paymentTypeLock = new();

    // Cache currency IDs per code (e.g. "EUR" → 123)
    private static readonly Dictionary<string, long> _currencyCache = new();
    private static readonly object _currencyLock = new();

    public PaymentHandler(InvoiceHandler invoiceHandler, ILogger<PaymentHandler> logger)
    {
        _invoiceHandler = invoiceHandler;
        _logger = logger;
    }

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Detect which variant of register_payment this is:
        // 1. Reversal: action=reverse → find existing paid invoice, reverse payment
        // 2. FX payment: has currency + exchange rates → create invoice in foreign currency, pay with FX
        // 3. Full chain: has orderLines → create customer→order→invoice→pay (existing path)
        // 4. Simple pay: no orderLines → find existing unpaid invoice, register payment

        if (extracted.Action == "reverse")
        {
            _logger.LogInformation("Payment variant: REVERSAL");
            return await HandleReversalAsync(api, extracted);
        }

        // Check for FX payment: look for currency, exchangeRateAtInvoice, exchangeRateAtPayment
        if (IsFxPayment(extracted))
        {
            _logger.LogInformation("Payment variant: FX PAYMENT (foreign currency)");
            return await HandleFxPaymentAsync(api, extracted);
        }

        // Check for reminder fees: composite task (find overdue invoice + voucher + invoice + partial payment)
        if (IsReminderFeeTask(extracted))
        {
            _logger.LogInformation("Payment variant: REMINDER FEES (find overdue + voucher + invoice + partial payment)");
            return await HandleReminderFeesAsync(api, extracted);
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

    private static bool IsFxPayment(ExtractionResult extracted)
    {
        var payment = extracted.Entities.GetValueOrDefault("payment");
        var invoice = extracted.Entities.GetValueOrDefault("invoice");
        // Check payment entity for currency/exchange rate fields
        if (payment != null && (payment.ContainsKey("currency") || payment.ContainsKey("exchangeRateAtPayment")))
            return true;
        // Check invoice entity for currency
        if (invoice != null && (invoice.ContainsKey("currency") || invoice.ContainsKey("exchangeRateAtInvoice")))
            return true;
        return false;
    }

    /// <summary>Detect composite "reminder fees" task: voucher postings + payment + unknown customer</summary>
    private static bool IsReminderFeeTask(ExtractionResult extracted)
    {
        // Must have voucher1 entity with debit/credit accounts (indicates accounting entry for fee)
        var voucher1 = extracted.Entities.GetValueOrDefault("voucher1");
        if (voucher1 == null) return false;
        if (!voucher1.ContainsKey("debitAccount") || !voucher1.ContainsKey("creditAccount")) return false;

        // Must have payment data (LLM may name it "payment" or "payment1")
        if (!extracted.Entities.ContainsKey("payment") && !extracted.Entities.ContainsKey("payment1")) return false;

        // Customer should be unknown/missing (need to discover from overdue invoice)
        var customerName = extracted.Relationships.GetValueOrDefault("customer");
        return string.IsNullOrEmpty(customerName)
            || customerName.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Composite reminder fees flow:
    /// 1. Find overdue invoice  2. Create voucher (debit receivables / credit reminder revenue)
    /// 3. Create reminder fee invoice for the customer  4. Register partial payment on overdue invoice
    /// </summary>
    private async Task<HandlerResult> HandleReminderFeesAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        // Step 1: Find overdue invoice (amountOutstanding > 0)
        var invoices = await api.GetAsync("/invoice", new Dictionary<string, string>
        {
            ["invoiceDateFrom"] = "2020-01-01",
            ["invoiceDateTo"] = today,
            ["from"] = "0",
            ["count"] = "100",
            ["fields"] = "id,customer(id),amount,amountOutstanding,invoiceDueDate"
        });

        long overdueInvoiceId = 0;
        long customerId = 0;
        decimal amountOutstanding = 0;

        if (invoices.TryGetProperty("values", out var vals))
        {
            foreach (var inv in vals.EnumerateArray())
            {
                var outstanding = inv.TryGetProperty("amountOutstanding", out var ao)
                    && ao.ValueKind == JsonValueKind.Number ? ao.GetDecimal() : 0m;
                if (outstanding > 0)
                {
                    overdueInvoiceId = inv.GetProperty("id").GetInt64();
                    amountOutstanding = outstanding;
                    if (inv.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object
                        && cust.TryGetProperty("id", out var custId) && custId.ValueKind == JsonValueKind.Number)
                    {
                        customerId = custId.GetInt64();
                    }
                    _logger.LogInformation("Found overdue invoice {Id}, customer {CustId}, outstanding={Outstanding}",
                        overdueInvoiceId, customerId, amountOutstanding);
                    break;
                }
            }
        }

        if (overdueInvoiceId == 0)
        {
            _logger.LogWarning("No overdue invoice found, falling back to full chain payment");
            return await HandleFullChainPaymentAsync(api, extracted);
        }

        // Step 2: Extract voucher and payment details
        var voucher1 = extracted.Entities.GetValueOrDefault("voucher1") ?? new();
        var debitAccountNum = GetStringField(voucher1, "debitAccount") ?? "1500";
        var creditAccountNum = GetStringField(voucher1, "creditAccount") ?? "3400";
        var feeAmount = ParseDecimalField(voucher1, "amount") ?? 50m;
        var feeDescription = GetStringField(voucher1, "description") ?? "Reminder fee";

        // Step 3: Resolve accounts + payment type in parallel
        var debitTask = api.GetAsync("/ledger/account", new Dictionary<string, string>
        { ["number"] = debitAccountNum, ["count"] = "1", ["fields"] = "id" });
        var creditTask = api.GetAsync("/ledger/account", new Dictionary<string, string>
        { ["number"] = creditAccountNum, ["count"] = "1", ["fields"] = "id" });
        var paymentTypeTask = ResolvePaymentTypeId(api);

        await Task.WhenAll(debitTask, creditTask);
        var debitResult = debitTask.Result;
        var creditResult = creditTask.Result;

        long debitAccountId = 0, creditAccountId = 0;
        if (debitResult.TryGetProperty("values", out var dv) && dv.GetArrayLength() > 0)
            debitAccountId = dv[0].GetProperty("id").GetInt64();
        if (creditResult.TryGetProperty("values", out var cv) && cv.GetArrayLength() > 0)
            creditAccountId = cv[0].GetProperty("id").GetInt64();

        // Step 4: Create voucher for reminder fee (non-fatal — account 1500 may be system-managed)
        if (debitAccountId > 0 && creditAccountId > 0)
        {
            try
            {
                var postings = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["date"] = today,
                        ["description"] = feeDescription,
                        ["account"] = new { id = debitAccountId },
                        ["amountGross"] = feeAmount,
                        ["amountGrossCurrency"] = feeAmount
                    },
                    new Dictionary<string, object>
                    {
                        ["date"] = today,
                        ["description"] = feeDescription,
                        ["account"] = new { id = creditAccountId },
                        ["amountGross"] = -feeAmount,
                        ["amountGrossCurrency"] = -feeAmount
                    }
                };
                var voucherBody = new Dictionary<string, object>
                {
                    ["date"] = today,
                    ["description"] = feeDescription,
                    ["postings"] = postings
                };
                await api.PostAsync("/ledger/voucher?sendToLedger=true", voucherBody);
                _logger.LogInformation("Created reminder fee voucher: debit {Debit} / credit {Credit}, amount {Amount}",
                    debitAccountNum, creditAccountNum, feeAmount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Voucher creation failed (control account restriction) — reminder fee invoice will handle accounting");
            }
        }

        // Step 5: Create reminder fee invoice for the customer (order → invoice → send)
        if (customerId > 0)
        {
            try
            {
                var vatTypeId = InvoiceHandler.DefaultOutputVatTypeId; // 25% standard
                var orderBody = new Dictionary<string, object>
                {
                    ["customer"] = new { id = customerId },
                    ["orderDate"] = today,
                    ["deliveryDate"] = today,
                    ["orderLines"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["description"] = feeDescription,
                            ["count"] = 1,
                            ["unitPriceIncludingVatCurrency"] = feeAmount,
                            ["vatType"] = new { id = vatTypeId }
                        }
                    },
                    ["isPrioritizeAmountsIncludingVat"] = true
                };

                JsonElement orderResult;
                try
                {
                    orderResult = await api.PostAsync("/order", orderBody);
                }
                catch (TripletexApiException ex) when (ex.StatusCode == 422 && ex.Message.Contains("mva-kode", StringComparison.OrdinalIgnoreCase))
                {
                    // Hardcoded VAT IDs invalid — use InvoiceHandler's full dynamic VAT resolver
                    _logger.LogWarning("Hardcoded VAT rejected, resolving via InvoiceHandler");
                    var (dynVatId, _) = await _invoiceHandler.ResolveVatTypesFull(api);
                    vatTypeId = dynVatId;
                    _logger.LogInformation("Resolved dynamic output VAT type ID: {Id}", vatTypeId);
                    ((Dictionary<string, object>)((object[])orderBody["orderLines"])[0])["vatType"] = new { id = vatTypeId };
                    orderResult = await api.PostAsync("/order", orderBody);
                }

                var orderId = orderResult.GetProperty("value").GetProperty("id").GetInt64();

                var invoiceBody = new Dictionary<string, object>
                {
                    ["invoiceDate"] = today,
                    ["invoiceDueDate"] = DateTime.Today.AddDays(14).ToString("yyyy-MM-dd"),
                    ["orders"] = new[] { new { id = orderId } }
                };
                var invoiceResult = await api.PostAsync("/invoice", invoiceBody);
                var reminderInvoiceId = invoiceResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created reminder fee invoice {InvoiceId} for customer {CustId}", reminderInvoiceId, customerId);

                // Send the invoice
                try
                {
                    await api.PutAsync($"/invoice/{reminderInvoiceId}/:send", body: null,
                        queryParams: new Dictionary<string, string> { ["sendType"] = "EMAIL" });
                }
                catch
                {
                    try
                    {
                        await api.PutAsync($"/invoice/{reminderInvoiceId}/:send", body: null,
                            queryParams: new Dictionary<string, string> { ["sendType"] = "MANUAL" });
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "Failed to send reminder invoice (non-fatal)");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create reminder fee invoice (non-fatal)");
            }
        }

        // Step 6: Register partial payment on the overdue invoice
        var payment = extracted.Entities.GetValueOrDefault("payment")
            ?? extracted.Entities.GetValueOrDefault("payment1") ?? new();
        var partialAmount = ParseDecimalField(payment, "amount") ?? amountOutstanding;
        var paymentTypeId = await paymentTypeTask;
        var paymentDate = ResolvePaymentDate(extracted);

        await RegisterPayment(api, overdueInvoiceId, paymentTypeId, partialAmount, paymentDate);
        _logger.LogInformation("Registered partial payment of {Amount} on overdue invoice {Id}",
            partialAmount, overdueInvoiceId);

        return PaymentResult(overdueInvoiceId, partialAmount);
    }

    /// <summary>FX payment: create invoice in foreign currency, register payment with exchange rate diff (agio/disagio)</summary>
    private async Task<HandlerResult> HandleFxPaymentAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var payment = extracted.Entities.GetValueOrDefault("payment") ?? new();
        var invoice = extracted.Entities.GetValueOrDefault("invoice") ?? new();

        // Extract FX fields from either payment or invoice entity
        var currencyCode = GetStringField(payment, "currency")
            ?? GetStringField(invoice, "currency")
            ?? "EUR";

        var foreignAmount = ParseDecimalField(payment, "amount")
            ?? ParseDecimalField(invoice, "amount")
            ?? extracted.RawAmounts.Where(a => decimal.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                .Select(a => decimal.Parse(a, NumberStyles.Any, CultureInfo.InvariantCulture))
                .FirstOrDefault();

        var rateAtInvoice = ParseDecimalField(payment, "exchangeRateAtInvoice")
            ?? ParseDecimalField(invoice, "exchangeRateAtInvoice")
            ?? 1m;
        var rateAtPayment = ParseDecimalField(payment, "exchangeRateAtPayment")
            ?? ParseDecimalField(invoice, "exchangeRateAtPayment")
            ?? rateAtInvoice;

        _logger.LogInformation("FX payment: {Amount} {Currency}, rate at invoice: {RateInv}, rate at payment: {RatePay}",
            foreignAmount, currencyCode, rateAtInvoice, rateAtPayment);

        // Resolve currency ID, payment type, and create invoice chain concurrently
        var currencyTask = ResolveCurrencyId(api, currencyCode);
        var paymentTypeTask = ResolvePaymentTypeId(api);

        var currencyId = await currencyTask;

        // Create invoice chain with foreign currency
        var (invoiceId, invoiceAmount) = await _invoiceHandler.CreateInvoiceChainAsync(api, extracted, currencyId);
        var paymentTypeId = await paymentTypeTask;

        // paidAmount = NOK amount (foreign × rate at payment)
        // paidAmountCurrency = foreign amount (what the customer actually paid in their currency)
        var nokAmount = foreignAmount * rateAtPayment;
        var paymentDate = ResolvePaymentDate(extracted);

        _logger.LogInformation("FX payment: NOK amount = {Nok}, foreign amount = {Foreign} {Currency}", nokAmount, foreignAmount, currencyCode);

        await RegisterPayment(api, invoiceId, paymentTypeId, nokAmount, paymentDate, paidAmountCurrency: foreignAmount);
        return PaymentResult(invoiceId, nokAmount);
    }

    private static decimal? ParseDecimalField(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val is null) return null;
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetDecimal();
            if (je.ValueKind == JsonValueKind.String && decimal.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        }
        if (val is decimal dec) return dec;
        if (val is double dbl) return (decimal)dbl;
        if (decimal.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    /// <summary>Full chain: Customer → Order → Invoice → Payment (original path)</summary>
    private async Task<HandlerResult> HandleFullChainPaymentAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Fetch payment type concurrently while creating the invoice chain
        var paymentTypeTask = ResolvePaymentTypeId(api);
        var (invoiceId, invoiceAmount) = await _invoiceHandler.CreateInvoiceChainAsync(api, extracted);
        var paymentTypeId = await paymentTypeTask;

        // Use amount from invoice POST response if available; otherwise do the extra GET
        var paidAmount = invoiceAmount > 0 ? invoiceAmount : await GetAmountOutstanding(api, invoiceId);

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
                ["invoiceDateFrom"] = "2020-01-01",
                ["invoiceDateTo"] = DateTime.Today.ToString("yyyy-MM-dd"),
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

    /// <summary>Reversal: find existing paid invoice, reverse payment via negative payment or credit note</summary>
    private async Task<HandlerResult> HandleReversalAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var customerId = await FindCustomerId(api, extracted);
        if (customerId.HasValue)
        {
            var invoices = await api.GetAsync("/invoice", new Dictionary<string, string>
            {
                ["customerId"] = customerId.Value.ToString(),
                ["invoiceDateFrom"] = "2020-01-01",
                ["invoiceDateTo"] = DateTime.Today.ToString("yyyy-MM-dd"),
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

                        // Try negative payment first — directly restores amountOutstanding
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
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Negative payment failed for invoice {Id}, trying credit note", invoiceId);
                        }

                        // Fallback: credit note (nullifies the invoice, should restore amountOutstanding)
                        try
                        {
                            var creditDate = ResolvePaymentDate(extracted);
                            await api.PutAsync($"/invoice/{invoiceId}/:createCreditNote",
                                body: null,
                                queryParams: new Dictionary<string, string>
                                {
                                    ["date"] = creditDate,
                                    ["comment"] = "Payment reversal",
                                    ["sendToCustomer"] = "false"
                                });
                            _logger.LogInformation("Created credit note for invoice {Id}", invoiceId);
                            return new HandlerResult
                            {
                                EntityType = "invoice",
                                EntityId = invoiceId,
                                Metadata = { ["paymentReversed"] = "true", ["method"] = "creditNote" }
                            };
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogWarning(ex2, "Credit note also failed for invoice {Id}", invoiceId);
                        }
                    }
                }
                _logger.LogWarning("No paid invoices found for customer {Id}", customerId);
            }
        }

        // Fallback for fresh environment: create invoice chain → pay it → then reverse
        // so that amountOutstanding > 0 (the "payment returned by bank" state)
        _logger.LogWarning("No paid invoice found — creating chain, paying, then reversing");
        return await HandleFullChainThenReverseAsync(api, extracted);
    }

    /// <summary>
    /// For "payment returned by bank" in a fresh environment:
    /// 1. Create invoice chain  2. Pay fully  3. Reverse with negative payment
    /// End state: amountOutstanding == invoice total (payment returned).
    /// </summary>
    private async Task<HandlerResult> HandleFullChainThenReverseAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var paymentTypeTask = ResolvePaymentTypeId(api);
        var (invoiceId, invoiceAmount) = await _invoiceHandler.CreateInvoiceChainAsync(api, extracted);
        var paymentTypeId = await paymentTypeTask;

        // Use amount from invoice POST response if available
        var amount = invoiceAmount > 0 ? invoiceAmount : await GetAmountOutstanding(api, invoiceId);
        var paymentDate = ResolvePaymentDate(extracted);
        await RegisterPayment(api, invoiceId, paymentTypeId, amount, paymentDate);
        _logger.LogInformation("Paid invoice {Id} amount={Amount} — now reversing", invoiceId, amount);

        // Reverse via negative payment
        try
        {
            await RegisterPayment(api, invoiceId, paymentTypeId, -amount, paymentDate);
            _logger.LogInformation("Reversed payment on invoice {Id} with negative amount {Amount}", invoiceId, -amount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Negative payment failed, trying credit note for invoice {Id}", invoiceId);
            await api.PutAsync($"/invoice/{invoiceId}/:createCreditNote",
                body: null,
                queryParams: new Dictionary<string, string>
                {
                    ["date"] = paymentDate,
                    ["comment"] = "Payment reversal",
                    ["sendToCustomer"] = "false"
                });
        }

        return new HandlerResult
        {
            EntityType = "invoice",
            EntityId = invoiceId,
            Metadata = { ["paymentReversed"] = "true", ["method"] = "payThenReverse" }
        };
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

    private async Task RegisterPayment(TripletexApiClient api, long invoiceId, long paymentTypeId, decimal paidAmount, string paymentDate, decimal? paidAmountCurrency = null)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["paymentDate"] = paymentDate,
            ["paymentTypeId"] = paymentTypeId.ToString(),
            ["paidAmount"] = paidAmount.ToString("F2", CultureInfo.InvariantCulture)
        };
        if (paidAmountCurrency.HasValue)
            queryParams["paidAmountCurrency"] = paidAmountCurrency.Value.ToString("F2", CultureInfo.InvariantCulture);

        await api.PutAsync(
            $"/invoice/{invoiceId}/:payment",
            body: null,
            queryParams: queryParams);
        _logger.LogInformation("Registered payment of {Amount} (currency: {CurrencyAmount}) on invoice {InvoiceId}",
            paidAmount, paidAmountCurrency?.ToString("F2") ?? "N/A", invoiceId);
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
        // Check session cache first — payment types are constant within a clean environment
        lock (_paymentTypeLock)
        {
            if (_paymentTypeCache.TryGetValue(api.SessionHash, out var cached))
                return cached;
        }

        // Dynamic lookup — IDs vary per environment, never hardcode
        return await ResolvePaymentTypeIdDynamic(api);
    }

    private async Task<long> ResolvePaymentTypeIdDynamic(TripletexApiClient api)
    {
        var result = await api.GetAsync("/invoice/paymentType", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,description"
        });

        long typeId = 0;
        if (result.TryGetProperty("values", out var types))
        {
            // Prefer "bank" or "overføring" type
            foreach (var t in types.EnumerateArray())
            {
                if (t.TryGetProperty("description", out var desc))
                {
                    var d = desc.GetString()?.ToLowerInvariant() ?? "";
                    if (d.Contains("bank") || d.Contains("overf"))
                    {
                        typeId = t.GetProperty("id").GetInt64();
                        break;
                    }
                }
            }
            if (typeId == 0)
            {
                foreach (var t in types.EnumerateArray())
                {
                    typeId = t.GetProperty("id").GetInt64();
                    break;
                }
            }
        }

        if (typeId == 0)
        {
            _logger.LogWarning("No payment types found via /invoice/paymentType — falling back to typeId=1");
            typeId = 1;
        }

        _logger.LogInformation("Resolved paymentTypeId={Id} for session", typeId);

        lock (_paymentTypeLock)
        {
            _paymentTypeCache[api.SessionHash] = typeId;
        }
        return typeId;
    }

    private async Task<long> ResolveCurrencyId(TripletexApiClient api, string currencyCode)
    {
        var key = currencyCode.ToUpperInvariant();
        lock (_currencyLock)
        {
            if (_currencyCache.TryGetValue(key, out var cached))
                return cached;
        }

        var result = await api.GetAsync("/currency", new Dictionary<string, string>
        {
            ["code"] = key,
            ["count"] = "1",
            ["fields"] = "id,code"
        });

        long currencyId = 0;
        if (result.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
        {
            currencyId = vals[0].GetProperty("id").GetInt64();
            _logger.LogInformation("Resolved currency {Code} → ID {Id}", key, currencyId);
        }
        else
        {
            _logger.LogWarning("Currency {Code} not found in Tripletex", key);
        }

        lock (_currencyLock)
        {
            _currencyCache[key] = currencyId;
        }
        return currencyId;
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
