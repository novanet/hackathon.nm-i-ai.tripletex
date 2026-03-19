using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class VoucherHandler : ITaskHandler
{
    private readonly ILogger<VoucherHandler> _logger;

    public VoucherHandler(ILogger<VoucherHandler> logger) => _logger = logger;

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var voucher = extracted.Entities.GetValueOrDefault("voucher") ?? new();
        var postingsEntity = extracted.Entities.GetValueOrDefault("postings") ?? new();

        var date = GetStringField(voucher, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd"));

        var description = GetStringField(voucher, "description") ?? "Bilag";

        // Build postings
        var postings = new List<Dictionary<string, object>>();

        // Check for postings nested inside the voucher entity (LLM often does this)
        if (voucher.TryGetValue("postings", out var pVal) && pVal is JsonElement pArr && pArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pArr.EnumerateArray())
            {
                var posting = new Dictionary<string, object> { ["date"] = date };
                if (item.TryGetProperty("description", out var d)) posting["description"] = d.GetString()!;
                if (item.TryGetProperty("accountNumber", out var an))
                {
                    var accStr = an.ValueKind == JsonValueKind.Number ? an.GetInt64().ToString() : an.GetString()!;
                    var accId = await ResolveAccountId(api, accStr);
                    if (accId.HasValue) posting["account"] = new { id = accId.Value };
                }
                else if (item.TryGetProperty("account", out var acc))
                {
                    var accStr = acc.ValueKind == JsonValueKind.Number ? acc.GetInt64().ToString() : acc.GetString()!;
                    if (accStr.Length <= 4)
                    {
                        var accId = await ResolveAccountId(api, accStr);
                        if (accId.HasValue) posting["account"] = new { id = accId.Value };
                    }
                }
                if (item.TryGetProperty("amountGross", out var ag))
                    posting["amountGross"] = ag.ValueKind == JsonValueKind.Number ? ag.GetDouble() : double.Parse(ag.GetString()!, CultureInfo.InvariantCulture);
                if (item.TryGetProperty("amount", out var am))
                    posting["amountGross"] = am.ValueKind == JsonValueKind.Number ? am.GetDouble() : double.Parse(am.GetString()!, CultureInfo.InvariantCulture);
                postings.Add(posting);
            }
        }

        foreach (var (key, val) in postingsEntity)
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                var posting = new Dictionary<string, object> { ["date"] = date };

                if (je.TryGetProperty("description", out var desc))
                    posting["description"] = desc.GetString()!;

                // Resolve account by number
                if (je.TryGetProperty("accountNumber", out var accNum))
                {
                    var accountId = await ResolveAccountId(api, accNum.GetString()!);
                    if (accountId.HasValue)
                        posting["account"] = new { id = accountId.Value };
                }
                else if (je.TryGetProperty("account", out var acc))
                {
                    // Could be an ID directly or a number
                    var accStr = acc.GetString()!;
                    if (int.TryParse(accStr, out var accId) && accStr.Length <= 4)
                    {
                        // Likely an account number
                        var resolvedId = await ResolveAccountId(api, accStr);
                        if (resolvedId.HasValue)
                            posting["account"] = new { id = resolvedId.Value };
                    }
                    else
                    {
                        posting["account"] = new { id = long.Parse(accStr) };
                    }
                }

                if (je.TryGetProperty("amount", out var amt))
                    posting["amountGross"] = amt.GetDouble();
                if (je.TryGetProperty("amountGross", out var amtGross))
                    posting["amountGross"] = amtGross.GetDouble();

                postings.Add(posting);
            }
        }

        // If no structured postings, try to build from debit/credit in voucher entity
        if (postings.Count == 0)
        {
            var debitAccount = GetStringField(voucher, "debitAccount") ?? GetStringField(voucher, "debit_account");
            var creditAccount = GetStringField(voucher, "creditAccount") ?? GetStringField(voucher, "credit_account");

            // Try debit/credit-specific amounts, then fall back to raw amounts
            decimal amount = 0m;
            var debitAmtStr = GetStringField(voucher, "debit_amount") ?? GetStringField(voucher, "debitAmount") ?? GetStringField(voucher, "amount");
            var creditAmtStr = GetStringField(voucher, "credit_amount") ?? GetStringField(voucher, "creditAmount");

            if (debitAmtStr != null && decimal.TryParse(debitAmtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var da))
                amount = da;
            else if (extracted.RawAmounts.Count > 0 && decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var ra))
                amount = ra;

            if (debitAccount != null)
            {
                var debitId = await ResolveAccountId(api, debitAccount);
                if (debitId.HasValue)
                {
                    postings.Add(new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = debitId.Value },
                        ["amountGross"] = amount
                    });
                }
            }

            if (creditAccount != null)
            {
                var creditId = await ResolveAccountId(api, creditAccount);
                if (creditId.HasValue)
                {
                    postings.Add(new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = creditId.Value },
                        ["amountGross"] = -amount
                    });
                }
            }
        }

        // Assign row numbers starting from 1 (row 0 is system-generated)
        // Also set amountGrossCurrency = amountGross for NOK postings
        for (int i = 0; i < postings.Count; i++)
        {
            postings[i]["row"] = i + 1;
            if (postings[i].TryGetValue("amountGross", out var ag))
                postings[i]["amountGrossCurrency"] = ag;
        }

        var body = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["postings"] = postings
        };

        _logger.LogInformation("Creating voucher: {Description} with {PostingCount} postings", description, postings.Count);

        var queryParams = new Dictionary<string, string> { ["sendToLedger"] = "true" };
        var result = await api.PostAsync($"/ledger/voucher?sendToLedger=true", body);
        var voucherId = result.GetProperty("value").GetProperty("id").GetInt64();

        _logger.LogInformation("Created voucher ID: {Id}", voucherId);
    }

    private async Task<long?> ResolveAccountId(TripletexApiClient api, string accountNumber)
    {
        var result = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["number"] = accountNumber,
            ["count"] = "1",
            ["fields"] = "id,number"
        });
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
                return v.GetProperty("id").GetInt64();
        }
        return null;
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
