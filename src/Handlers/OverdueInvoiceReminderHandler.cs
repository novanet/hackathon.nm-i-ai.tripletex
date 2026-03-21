using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles Task 25: Overdue invoice reminder.
/// Finds all overdue unpaid invoices and:
/// 1. Creates a reminder fee voucher (debit 1500 receivables / credit 3400 or specified income account)
/// 2. Updates invoice dueDate if a new due date is specified
/// The competition typically provides a specific invoice reference (customer name or invoice number).
/// </summary>
public class OverdueInvoiceReminderHandler : ITaskHandler
{
    private readonly ILogger<OverdueInvoiceReminderHandler> _logger;

    public OverdueInvoiceReminderHandler(ILogger<OverdueInvoiceReminderHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var result = new HandlerResult { EntityType = "voucher" };

        var reminderEntity = extracted.Entities.GetValueOrDefault("reminder")
            ?? extracted.Entities.GetValueOrDefault("invoice")
            ?? new();

        // Extract parameters
        var customerName = GetStringField(reminderEntity, "customerName")
            ?? GetStringField(reminderEntity, "customer");
        var reminderFee = GetDecimalField(reminderEntity, "reminderFee")
            ?? GetDecimalField(reminderEntity, "fee")
            ?? GetDecimalField(reminderEntity, "amount");
        // If still null, look in raw amounts
        if (reminderFee == null && extracted.RawAmounts.Count > 0)
            decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var fa);
        if (reminderFee == null && extracted.RawAmounts.Count > 0
            && decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedFee))
            reminderFee = parsedFee;

        var newDueDate = GetStringField(reminderEntity, "newDueDate")
            ?? GetStringField(reminderEntity, "dueDate");
        if (newDueDate == null && extracted.Dates.Count > 0)
            newDueDate = extracted.Dates[0];

        var date = GetStringField(reminderEntity, "date")
            ?? DateTime.Today.ToString("yyyy-MM-dd");

        // Income account for reminder fee (default 3400 "purregebyr" / "reminder fee")
        var incomeAccountNumber = GetStringField(reminderEntity, "incomeAccount") ?? "3400";
        // Receivables account for reminder fee (default 1500 AR)
        var receivablesAccountNumber = GetStringField(reminderEntity, "receivablesAccount") ?? "1500";

        _logger.LogInformation("Reminder handler: customer={Customer}, fee={Fee}, newDueDate={NewDue}, date={Date}",
            customerName, reminderFee, newDueDate, date);

        // Step 1: Find overdue invoices
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var overdueInvoices = await FindOverdueInvoices(api, customerName, today);
        _logger.LogInformation("Found {Count} overdue invoices total", overdueInvoices.Count);

        if (overdueInvoices.Count == 0)
        {
            _logger.LogWarning("No overdue invoices found — creating reminder voucher anyway");
        }

        // Step 2: Create reminder fee voucher
        if (reminderFee.HasValue && reminderFee.Value > 0)
        {
            var voucherId = await CreateReminderFeeVoucher(api, date, reminderFee.Value, receivablesAccountNumber, incomeAccountNumber, customerName, overdueInvoices);
            result.EntityId = voucherId;
            _logger.LogInformation("Created reminder fee voucher ID: {Id}", voucherId);
        }

        // Step 3: Update due dates and send reminders for overdue invoices
        foreach (var invoice in overdueInvoices)
        {
            var invoiceId = invoice.InvoiceId;
            var invoiceVersion = invoice.Version;

            // Update invoice due date if a new one is specified
            if (!string.IsNullOrEmpty(newDueDate))
            {
                try
                {
                    // GET current invoice to get version
                    var invoiceData = await api.GetAsync($"/invoice/{invoiceId}", new Dictionary<string, string>
                    {
                        ["fields"] = "id,version,invoiceDate,invoiceDueDate,amountOutstanding"
                    });
                    if (invoiceData.TryGetProperty("value", out var inv))
                    {
                        var currentVersion = inv.GetProperty("version").GetInt32();
                        var currentDate = inv.TryGetProperty("invoiceDate", out var id) ? id.GetString()! : date;

                        await api.PutAsync($"/invoice/{invoiceId}", new Dictionary<string, object>
                        {
                            ["id"] = invoiceId,
                            ["version"] = currentVersion,
                            ["invoiceDate"] = currentDate,
                            ["invoiceDueDate"] = newDueDate
                        });
                        _logger.LogInformation("Updated invoice {Id} due date to {Date}", invoiceId, newDueDate);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to update invoice {Id} due date: {Msg}", invoiceId, ex.Message);
                }
            }
        }

        return result;
    }

    private async Task<List<(long InvoiceId, int Version, decimal AmountOutstanding)>> FindOverdueInvoices(
        TripletexApiClient api, string? customerName, string today)
    {
        var invoices = new List<(long InvoiceId, int Version, decimal AmountOutstanding)>();

        // Search for all sent invoices with outstanding amounts
        var queryParams = new Dictionary<string, string>
        {
            ["invoiceDateFrom"] = "2020-01-01",
            ["invoiceDateTo"] = today,
            ["count"] = "100",
            ["fields"] = "id,version,invoiceDueDate,amountOutstanding,customer(id,name)"
        };

        var response = await api.GetAsync("/invoice", queryParams);
        if (!response.TryGetProperty("values", out var values))
            return invoices;

        foreach (var inv in values.EnumerateArray())
        {
            decimal outstanding = 0;
            if (inv.TryGetProperty("amountOutstanding", out var ao) && ao.ValueKind == JsonValueKind.Number)
                outstanding = ao.GetDecimal();

            if (outstanding <= 0) continue;

            string? dueDate = null;
            if (inv.TryGetProperty("invoiceDueDate", out var dd) && dd.ValueKind == JsonValueKind.String)
                dueDate = dd.GetString();

            // Check if overdue (due date in the past)
            bool isOverdue = dueDate != null && string.Compare(dueDate, today, StringComparison.Ordinal) < 0;
            if (!isOverdue && !string.IsNullOrEmpty(dueDate))
            {
                // Also include invoices due today or in the past when customer name matches
                isOverdue = string.Compare(dueDate, today, StringComparison.Ordinal) <= 0;
            }

            if (!isOverdue) continue;

            // Filter by customer name if specified
            if (customerName != null)
            {
                string? invCustomerName = null;
                if (inv.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object)
                    invCustomerName = cust.TryGetProperty("name", out var cn) ? cn.GetString() : null;

                if (invCustomerName != null && !invCustomerName.Contains(customerName, StringComparison.OrdinalIgnoreCase)
                    && !customerName.Contains(invCustomerName, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var invoiceId = inv.GetProperty("id").GetInt64();
            var version = inv.TryGetProperty("version", out var ver) ? ver.GetInt32() : 0;
            invoices.Add((invoiceId, version, outstanding));
        }

        return invoices;
    }

    private async Task<long> CreateReminderFeeVoucher(
        TripletexApiClient api, string date, decimal fee,
        string receivablesAccountNumber, string incomeAccountNumber,
        string? customerName, List<(long InvoiceId, int Version, decimal AmountOutstanding)> overdueInvoices)
    {
        // Resolve accounts
        var (receivablesId, _, _) = await ResolveAccountId(api, receivablesAccountNumber);
        var (incomeId, _, _) = await ResolveAccountId(api, incomeAccountNumber);

        // If income account 3400 doesn't exist, try 3900 (other income)
        if (!incomeId.HasValue)
            (incomeId, _, _) = await ResolveAccountId(api, "3900");

        var description = customerName != null ? $"Purregebyr {customerName}" : "Purregebyr";

        var postings = new List<Dictionary<string, object>>();

        if (receivablesId.HasValue)
        {
            postings.Add(new Dictionary<string, object>
            {
                ["date"] = date,
                ["description"] = description,
                ["account"] = new { id = receivablesId.Value },
                ["amountGross"] = (double)fee,
                ["amountGrossCurrency"] = (double)fee,
                ["row"] = 1
            });
        }

        if (incomeId.HasValue)
        {
            postings.Add(new Dictionary<string, object>
            {
                ["date"] = date,
                ["description"] = description,
                ["account"] = new { id = incomeId.Value },
                ["amountGross"] = -(double)fee,
                ["amountGrossCurrency"] = -(double)fee,
                ["row"] = 2
            });
        }

        if (postings.Count < 2)
        {
            _logger.LogWarning("Could not resolve accounts for reminder voucher — postings incomplete, receivables={R}, income={I}",
                receivablesId, incomeId);
            // Still try to post what we have; Tripletex will reject with validation message
        }

        var body = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["postings"] = postings
        };

        var voucherResult = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
        return voucherResult.GetProperty("value").GetProperty("id").GetInt64();
    }

    private async Task<(long? accountId, long? vatTypeId, bool vatLocked)> ResolveAccountId(TripletexApiClient api, string accountNumber)
    {
        var result = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["number"] = accountNumber,
            ["count"] = "1",
            ["fields"] = "id,number,vatLocked,vatType(id)"
        });
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
            {
                var id = v.GetProperty("id").GetInt64();
                long? vatId = null;
                bool locked = v.TryGetProperty("vatLocked", out var vl) && vl.ValueKind == JsonValueKind.True;
                if (locked && v.TryGetProperty("vatType", out var vt) && vt.ValueKind == JsonValueKind.Object
                    && vt.TryGetProperty("id", out var vtId) && vtId.ValueKind == JsonValueKind.Number)
                    vatId = vtId.GetInt64();
                return (id, vatId, locked);
            }
        }
        return (null, null, false);
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

    private static decimal? GetDecimalField(Dictionary<string, object> dict, string key)
    {
        var str = GetStringField(dict, key);
        if (str != null && decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }
}
