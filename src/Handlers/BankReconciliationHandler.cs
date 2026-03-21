using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles bank_reconciliation tasks.
/// Creates a manual bank reconciliation entry for account 1920 (or specified account).
/// API chain:
///   1. GET /ledger/account?number=1920 → accountId
///   2. GET /bank/reconciliation?from=0&count=1 (check if open one exists)
///   3. POST /bank/reconciliation {account, bankAccountClosingBalanceCurrency, isApproved:false, date}
///   4. POST /bank/reconciliation/{id}/:match (optional — match unmatched transactions)
/// </summary>
public class BankReconciliationHandler : ITaskHandler
{
    private readonly ILogger<BankReconciliationHandler> _logger;

    public BankReconciliationHandler(ILogger<BankReconciliationHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var rec = extracted.Entities.GetValueOrDefault("reconciliation") ?? new();

        // Extract account number (default: 1920 = main bank account in Norwegian chart of accounts)
        var accountNumber = GetStringField(rec, "accountNumber")
            ?? GetStringField(rec, "account")
            ?? "1920";

        // Extract closing balance
        decimal? closingBalance = null;
        if (rec.TryGetValue("closingBalance", out var cbVal))
        {
            if (cbVal is JsonElement cbElem && cbElem.ValueKind == JsonValueKind.Number)
                closingBalance = cbElem.GetDecimal();
            else if (decimal.TryParse(cbVal?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                closingBalance = parsed;
        }
        // Fallback: try raw_amounts
        if (!closingBalance.HasValue && extracted.RawAmounts.Count > 0)
        {
            if (decimal.TryParse(extracted.RawAmounts[0].Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var rawParsed))
                closingBalance = rawParsed;
        }
        closingBalance ??= 0m;

        // Extract date (statement date / period end)
        var date = GetStringField(rec, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : null)
            ?? DateTime.Today.ToString("yyyy-MM-dd");

        _logger.LogInformation("BankReconciliation: account={Account}, balance={Balance}, date={Date}",
            accountNumber, closingBalance, date);

        // Step 1: Resolve account ID
        var accountResult = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["number"] = accountNumber,
            ["count"] = "1",
            ["fields"] = "id,number"
        });

        long? accountId = null;
        if (accountResult.TryGetProperty("values", out var acctVals))
        {
            foreach (var v in acctVals.EnumerateArray())
            {
                accountId = v.GetProperty("id").GetInt64();
                break;
            }
        }

        if (!accountId.HasValue)
        {
            _logger.LogWarning("Could not find account {Number} — aborting bank reconciliation", accountNumber);
            return HandlerResult.Empty;
        }

        // Step 2: Create manual bank reconciliation
        var body = new Dictionary<string, object>
        {
            ["account"] = new { id = accountId.Value },
            ["bankAccountClosingBalanceCurrency"] = closingBalance.Value,
            ["isApproved"] = false,
            ["date"] = date
        };

        var reconResult = await api.PostAsync("/bank/reconciliation", body);

        long? reconId = null;
        if (reconResult.TryGetProperty("value", out var reconVal))
            reconId = reconVal.GetProperty("id").GetInt64();

        _logger.LogInformation("Created bank reconciliation ID: {Id}", reconId);

        return new HandlerResult { EntityType = "reconciliation", EntityId = reconId };
    }

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
        return val?.ToString();
    }
}
