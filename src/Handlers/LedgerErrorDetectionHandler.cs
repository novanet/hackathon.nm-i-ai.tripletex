using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles Task 24: Ledger error detection and correction.
/// Detects common accounting errors in posted vouchers and creates correction vouchers.
/// 
/// Common error patterns to detect:
/// - Postings to wrong accounts (e.g., expense to income account)
/// - Unbalanced vouchers (sum of postings != 0)
/// - Duplicate postings (same amount, account, date)
/// - Wrong VAT account usage
/// 
/// Strategy: The competition prompt specifies the error explicitly.
/// We rely on LLM extraction to identify the error and correction needed.
/// </summary>
public class LedgerErrorDetectionHandler : ITaskHandler
{
    private readonly ILogger<LedgerErrorDetectionHandler> _logger;

    public LedgerErrorDetectionHandler(ILogger<LedgerErrorDetectionHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var result = new HandlerResult { EntityType = "voucher" };

        var correctionEntity = extracted.Entities.GetValueOrDefault("correction")
            ?? extracted.Entities.GetValueOrDefault("voucher")
            ?? new();

        // Extract correction details from LLM extraction
        var errorVoucherNumber = GetStringField(correctionEntity, "errorVoucherNumber")
            ?? GetStringField(correctionEntity, "voucherNumber");
        var wrongAccount = GetStringField(correctionEntity, "wrongAccount")
            ?? GetStringField(correctionEntity, "fromAccount");
        var correctAccount = GetStringField(correctionEntity, "correctAccount")
            ?? GetStringField(correctionEntity, "toAccount");
        var correctionAmount = GetDecimalField(correctionEntity, "amount")
            ?? GetDecimalField(correctionEntity, "correctionAmount");
        var date = GetStringField(correctionEntity, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Today.ToString("yyyy-MM-dd"));
        var description = GetStringField(correctionEntity, "description") ?? "Korrigering bilagsfeil";

        _logger.LogInformation("Ledger error detection: voucherRef={VRef}, from={From}, to={To}, amount={Amt}",
            errorVoucherNumber, wrongAccount, correctAccount, correctionAmount);

        // If correction details are fully known from LLM extraction, create correction voucher directly
        if (wrongAccount != null && correctAccount != null && correctionAmount.HasValue)
        {
            var voucherId = await CreateCorrectionVoucher(api, date, description, wrongAccount, correctAccount, correctionAmount.Value);
            result.EntityId = voucherId;
            _logger.LogInformation("Created correction voucher directly: ID={Id}", voucherId);
            return result;
        }

        // Otherwise, search for the erroneous voucher
        if (errorVoucherNumber != null || wrongAccount != null)
        {
            var errorVoucher = await FindErrorVoucher(api, errorVoucherNumber, wrongAccount, extracted);
            if (errorVoucher != null)
            {
                _logger.LogInformation("Found error voucher: ID={Id}, date={Date}", errorVoucher.VoucherId, errorVoucher.Date);

                if (wrongAccount == null) wrongAccount = errorVoucher.WrongAccount;
                if (correctAccount == null) correctAccount = errorVoucher.CorrectAccount;
                if (!correctionAmount.HasValue) correctionAmount = errorVoucher.Amount;

                if (wrongAccount != null && correctAccount != null && correctionAmount.HasValue)
                {
                    var corrDate = date ?? errorVoucher.Date;
                    var voucherId = await CreateCorrectionVoucher(api, corrDate, description, wrongAccount, correctAccount, correctionAmount.Value);
                    result.EntityId = voucherId;
                    _logger.LogInformation("Created correction voucher for error voucher {VRef}: ID={Id}", errorVoucherNumber, voucherId);
                    return result;
                }
            }
        }

        // Final fallback: Create reversal + re-post using all available entity data
        _logger.LogInformation("Creating correction voucher from available entity data");

        // Try postings array in correction entity
        if (correctionEntity.TryGetValue("postings", out var pVal) && pVal is JsonElement pArr && pArr.ValueKind == JsonValueKind.Array)
        {
            var postings = new List<Dictionary<string, object>>();
            int row = 1;
            foreach (var item in pArr.EnumerateArray())
            {
                var posting = await BuildPostingFromJson(api, item, date);
                if (posting.ContainsKey("amountGross"))
                {
                    posting["row"] = row++;
                    postings.Add(posting);
                }
            }

            if (postings.Count >= 2)
            {
                var body = new Dictionary<string, object>
                {
                    ["date"] = date,
                    ["description"] = description,
                    ["postings"] = postings
                };
                var voucherResult = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
                result.EntityId = voucherResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created correction voucher from postings array: ID={Id}", result.EntityId);
                return result;
            }
        }

        // Minimal fallback: debit/credit pair from entity
        var debitAcc = GetStringField(correctionEntity, "debitAccount") ?? correctAccount;
        var creditAcc = GetStringField(correctionEntity, "creditAccount") ?? wrongAccount;
        var amt = correctionAmount ?? (extracted.RawAmounts.Count > 0
            && decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var rAmt) ? rAmt : 0m);

        if (debitAcc != null && creditAcc != null && amt > 0)
        {
            var voucherId = await CreateCorrectionVoucher(api, date, description, creditAcc, debitAcc, amt);
            result.EntityId = voucherId;
            _logger.LogInformation("Created minimal correction voucher: ID={Id}", voucherId);
        }
        else
        {
            _logger.LogWarning("Insufficient data for correction voucher — returning empty result");
        }

        return result;
    }

    private async Task<long> CreateCorrectionVoucher(
        TripletexApiClient api, string date, string description,
        string wrongAccount, string correctAccount, decimal amount)
    {
        // Correction pattern: reverse the wrong posting and re-post to correct account
        // Row 1: Credit the wrong account (reverse original debit)
        // Row 2: Debit the correct account (post to correct)
        // This assumes original error was: debit wrongAccount / credit someOtherAccount

        var (wrongId, _, _) = await ResolveAccountId(api, wrongAccount);
        var (correctId, correctVatId, _) = await ResolveAccountId(api, correctAccount);

        if (!wrongId.HasValue || !correctId.HasValue)
        {
            _logger.LogWarning("Cannot resolve accounts: wrong={W}({WF}), correct={C}({CF})",
                wrongAccount, wrongId.HasValue, correctAccount, correctId.HasValue);
            throw new InvalidOperationException($"Cannot resolve correction accounts: wrong={wrongAccount}, correct={correctAccount}");
        }

        var postings = new[]
        {
            new Dictionary<string, object>
            {
                ["date"] = date,
                ["description"] = description,
                ["account"] = new { id = wrongId.Value },
                ["amountGross"] = -(double)amount,
                ["amountGrossCurrency"] = -(double)amount,
                ["row"] = 1
            },
            new Dictionary<string, object>
            {
                ["date"] = date,
                ["description"] = description,
                ["account"] = new { id = correctId.Value },
                ["amountGross"] = (double)amount,
                ["amountGrossCurrency"] = (double)amount,
                ["row"] = 2
            }
        };

        if (correctVatId.HasValue) postings[1]["vatType"] = new { id = correctVatId.Value };

        var body = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["postings"] = postings
        };

        var voucherResult = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
        return voucherResult.GetProperty("value").GetProperty("id").GetInt64();
    }

    private async Task<ErrorVoucherInfo?> FindErrorVoucher(
        TripletexApiClient api, string? voucherNumber, string? wrongAccount, ExtractionResult extracted)
    {
        // Search recent vouchers to find the erroneous one
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var fromDate = DateTime.Today.AddMonths(-3).ToString("yyyy-MM-dd");

        var queryParams = new Dictionary<string, string>
        {
            ["dateFrom"] = fromDate,
            ["dateTo"] = today,
            ["count"] = "100",
            ["fields"] = "id,date,description,voucher(id,voucherNumber)"
        };

        // Try to find by voucher/posting query with account filter
        if (!string.IsNullOrEmpty(wrongAccount))
        {
            var (accountId, _, _) = await ResolveAccountId(api, wrongAccount);
            if (accountId.HasValue)
            {
                var postingsResult = await api.GetAsync("/ledger/posting", new Dictionary<string, string>
                {
                    ["dateFrom"] = fromDate,
                    ["dateTo"] = today,
                    ["accountId"] = accountId.Value.ToString(),
                    ["count"] = "10",
                    ["fields"] = "id,date,amount,voucher(id,description)"
                });

                if (postingsResult.TryGetProperty("values", out var pVals) && pVals.GetArrayLength() > 0)
                {
                    var first = pVals[0];
                    var postingDate = first.TryGetProperty("date", out var pd) ? pd.GetString()! : today;
                    var postingAmt = first.TryGetProperty("amount", out var pa) && pa.ValueKind == JsonValueKind.Number
                        ? Math.Abs(pa.GetDecimal()) : 0m;

                    long? vId = null;
                    if (first.TryGetProperty("voucher", out var vRef) && vRef.ValueKind == JsonValueKind.Object
                        && vRef.TryGetProperty("id", out var vid))
                        vId = vid.GetInt64();

                    if (vId.HasValue && postingAmt > 0)
                    {
                        return new ErrorVoucherInfo
                        {
                            VoucherId = vId.Value,
                            Date = postingDate,
                            WrongAccount = wrongAccount,
                            Amount = postingAmt,
                            CorrectAccount = null // Will be determined from extracted data
                        };
                    }
                }
            }
        }

        return null;
    }

    private async Task<Dictionary<string, object>> BuildPostingFromJson(TripletexApiClient api, JsonElement item, string date)
    {
        var posting = new Dictionary<string, object> { ["date"] = date };
        if (item.TryGetProperty("description", out var d)) posting["description"] = d.GetString()!;

        string? accountStr = null;
        if (item.TryGetProperty("accountNumber", out var an))
            accountStr = an.ValueKind == JsonValueKind.Number ? an.GetInt64().ToString() : an.GetString()!;
        else if (item.TryGetProperty("account", out var acc))
            accountStr = acc.ValueKind == JsonValueKind.Number ? acc.GetInt64().ToString() : acc.GetString()!;

        if (accountStr != null)
        {
            var (accId, vatId, _) = await ResolveAccountId(api, accountStr);
            if (accId.HasValue) posting["account"] = new { id = accId.Value };
            if (vatId.HasValue) posting["vatType"] = new { id = vatId.Value };
        }

        if (item.TryGetProperty("amountGross", out var ag) && ag.ValueKind == JsonValueKind.Number)
        {
            var amt = ag.GetDouble();
            if (item.TryGetProperty("debitCredit", out var dc) && dc.ValueKind == JsonValueKind.String
                && dc.GetString()?.Equals("credit", StringComparison.OrdinalIgnoreCase) == true)
                amt = -Math.Abs(amt);
            posting["amountGross"] = amt;
            posting["amountGrossCurrency"] = amt;
        }
        else if (item.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number)
        {
            var amt = am.GetDouble();
            posting["amountGross"] = amt;
            posting["amountGrossCurrency"] = amt;
        }

        return posting;
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

    private record ErrorVoucherInfo
    {
        public long VoucherId { get; init; }
        public string Date { get; init; } = "";
        public string? WrongAccount { get; init; }
        public string? CorrectAccount { get; init; }
        public decimal Amount { get; init; }
    }
}
