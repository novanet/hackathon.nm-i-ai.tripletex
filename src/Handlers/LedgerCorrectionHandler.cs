using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class LedgerCorrectionHandler : ITaskHandler
{
    private readonly ILogger<LedgerCorrectionHandler> _logger;

    public LedgerCorrectionHandler(ILogger<LedgerCorrectionHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // 1. Parse correction entities (correction1, correction2, ...)
        var corrections = ParseCorrections(extracted);
        if (corrections.Count == 0)
        {
            _logger.LogWarning("No correction entities found in extraction for correct_ledger task");
            return HandlerResult.Empty;
        }
        _logger.LogInformation("Ledger correction: {Count} corrections to apply", corrections.Count);

        // 2. Determine date range — default Jan+Feb of current year
        var year = DateTime.Now.Year;
        var dateFrom = $"{year}-01-01";
        var dateTo = $"{year}-02-{(DateTime.IsLeapYear(year) ? "29" : "28")}";

        // 3. GET all postings in date range (1 free GET call — includes account id + number via fields param)
        var allPostings = await GetAllPostings(api, dateFrom, dateTo);
        _logger.LogInformation("Fetched {Count} postings for {From}..{To}", allPostings.Count, dateFrom, dateTo);

        // 4. Build complete account number ↔ id mapping (1 free GET call)
        var accountIdToNumber = new Dictionary<long, string>();
        var accountNumberToId = new Dictionary<string, long>();
        await LoadAccountMap(api, accountIdToNumber, accountNumberToId);

        // 5. Enrich postings: fill AccountNumber from map for any missing
        foreach (var p in allPostings)
        {
            if (p.AccountNumber == null && p.AccountId > 0 && accountIdToNumber.TryGetValue(p.AccountId, out var num))
                p.AccountNumber = num;
            // Also populate cache from postings
            if (p.AccountNumber != null && !accountNumberToId.ContainsKey(p.AccountNumber))
                accountNumberToId[p.AccountNumber] = p.AccountId;
        }

        // 6. Group postings by voucher id (for finding counter-accounts)
        var byVoucher = allPostings
            .GroupBy(p => p.VoucherId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 7. Post one correction voucher per error
        var correctionDate = DateTime.Now.ToString("yyyy-MM-dd");
        var createdIds = new List<long>();

        foreach (var correction in corrections)
        {
            try
            {
                var id = await PostCorrection(api, correction, allPostings, byVoucher, accountNumberToId, correctionDate);
                if (id.HasValue)
                    createdIds.Add(id.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping failed ledger correction {Type} for account {Account}", correction.ErrorType, correction.Account);
            }
        }

        _logger.LogInformation("Ledger correction complete: {Success}/{Total} vouchers created",
            createdIds.Count, corrections.Count);

        if (createdIds.Count == 0)
            return HandlerResult.Empty;

        return new HandlerResult
        {
            EntityType = "voucher",
            EntityId = createdIds[0],
            AdditionalEntityIds = createdIds.Skip(1).ToList()
        };
    }

    private async Task<long?> PostCorrection(
        TripletexApiClient api,
        CorrectionEntry c,
        List<PostingInfo> allPostings,
        Dictionary<long, List<PostingInfo>> byVoucher,
        Dictionary<string, long> numberToId,
        string date)
    {
        List<object> postings;
        string description;

        switch (c.ErrorType)
        {
            case "wrong_account":
                {
                    // Both account numbers given in the prompt — no voucher lookup needed
                    var wrongId = await ResolveId(api, c.Account, numberToId);
                    var correctId = await ResolveId(api, c.CorrectAccount!, numberToId);
                    if (wrongId == null || correctId == null)
                    {
                        _logger.LogWarning("Cannot resolve accounts for wrong_account: {W}→{C}", c.Account, c.CorrectAccount);
                        return null;
                    }
                    description = $"Korreksjon: feil konto {c.Account}→{c.CorrectAccount}";
                    // Reverse wrong account, add to correct account — both sum to 0
                    postings = new List<object>
                {
                    new { account = new { id = correctId.Value }, amountGross = (double)c.Amount, amountGrossCurrency = (double)c.Amount, row = 1, date, description },
                    new { account = new { id = wrongId.Value }, amountGross = -(double)c.Amount, amountGrossCurrency = -(double)c.Amount, row = 2, date, description }
                };
                    break;
                }

            case "duplicate":
                {
                    // Find the duplicate posting and its counter-account
                    var (ep, counterId) = FindAndGetCounter(c.Account, c.Amount, allPostings, byVoucher);
                    if (counterId == null)
                        counterId = await ResolveFallbackCounterId(api, c.Account, numberToId);

                    if (ep == null)
                        _logger.LogWarning("Duplicate fallback: no exact posting found for account={A} amount≈{Amt}; using generic counter account", c.Account, c.Amount);

                    if (counterId == null)
                    {
                        _logger.LogWarning("Cannot find duplicate posting fallback account: account={A} amount≈{Amt}", c.Account, c.Amount);
                        return null;
                    }
                    var errorId = await ResolveId(api, c.Account, numberToId);
                    if (errorId == null) return null;
                    description = $"Korreksjon: duplikatbilag konto {c.Account}";
                    // Reverse: counter gets +amount, error account gets -amount
                    postings = new List<object>
                {
                    new { account = new { id = counterId.Value }, amountGross = (double)c.Amount, amountGrossCurrency = (double)c.Amount, row = 1, date, description },
                    new { account = new { id = errorId.Value }, amountGross = -(double)c.Amount, amountGrossCurrency = -(double)c.Amount, row = 2, date, description }
                };
                    break;
                }

            case "missing_vat":
                {
                    // Missing VAT correction: the stated amount is NET (excl. VAT).
                    // Use vatType on the expense account posting so Tripletex auto-generates
                    // the VAT line on account 2710. Manual posting to 2710 does NOT satisfy competition checks.
                    var expenseId = await ResolveId(api, c.Account, numberToId);
                    if (expenseId == null)
                    {
                        _logger.LogWarning("Cannot resolve expense account {A} for missing_vat", c.Account);
                        return null;
                    }

                    // Gross = net × 1.25 (25% VAT). When posted with vatType, Tripletex splits:
                    // net (c.Amount) to expense account, VAT (c.Amount × 0.25) to 2710 automatically.
                    var grossAmt = Math.Round(c.Amount * 1.25m, 2);
                    description = $"Korreksjon: manglende mva konto {c.Account}";

                    // Posting 1: Debit expense with vatType → Tripletex auto-splits into net + VAT(2710)
                    // Posting 2: Credit expense without vatType → reverses the original gross-on-expense
                    // Net effect: expense reduced by VAT portion, 2710 gains the VAT amount
                    postings = new List<object>
                {
                    new { account = new { id = expenseId.Value }, amountGross = (double)grossAmt, amountGrossCurrency = (double)grossAmt, vatType = new { id = 1 }, row = 1, date, description },
                    new { account = new { id = expenseId.Value }, amountGross = -(double)grossAmt, amountGrossCurrency = -(double)grossAmt, row = 2, date, description }
                };
                    break;
                }

            case "wrong_amount":
                {
                    var posted = c.PostedAmount ?? c.Amount;
                    var correct = c.CorrectAmount ?? c.Amount;
                    var diff = posted - correct; // positive = over-posted, negative = under-posted
                    if (Math.Abs(diff) < 0.01m)
                    {
                        _logger.LogWarning("Posted and correct amounts are equal for wrong_amount on {A}", c.Account);
                        return null;
                    }
                    var (_, counterId) = FindAndGetCounter(c.Account, posted, allPostings, byVoucher);
                    if (counterId == null)
                        counterId = await ResolveFallbackCounterId(api, c.Account, numberToId);
                    var errorId = await ResolveId(api, c.Account, numberToId);
                    if (errorId == null || counterId == null)
                    {
                        _logger.LogWarning("Cannot find wrong_amount posting: account={A} posted={P}", c.Account, posted);
                        return null;
                    }
                    description = $"Korreksjon: feil beløp konto {c.Account}";
                    // Reverse the excess: counter-account gets +diff, error account gets -diff
                    postings = new List<object>
                {
                    new { account = new { id = counterId.Value }, amountGross = (double)diff, amountGrossCurrency = (double)diff, row = 1, date, description },
                    new { account = new { id = errorId.Value }, amountGross = -(double)diff, amountGrossCurrency = -(double)diff, row = 2, date, description }
                };
                    break;
                }

            default:
                _logger.LogWarning("Unknown error type: {T}", c.ErrorType);
                return null;
        }

        var body = new { date, description, postings };
        var resp = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
        if (resp.TryGetProperty("value", out var val) && val.TryGetProperty("id", out var idProp))
        {
            var id = idProp.GetInt64();
            _logger.LogInformation("Created correction voucher {Id} ({Type}: account={Acct})", id, c.ErrorType, c.Account);
            return id;
        }
        _logger.LogWarning("Failed to create correction voucher for {Type}/{Acct}", c.ErrorType, c.Account);
        return null;
    }

    /// <summary>
    /// Find the posting matching (accountNumber, amount) and return it plus the ID of its counter-posting account.
    /// </summary>
    private (PostingInfo? posting, long? counterId) FindAndGetCounter(
        string accountNumber, decimal amount,
        List<PostingInfo> allPostings,
        Dictionary<long, List<PostingInfo>> byVoucher)
    {
        // Match by account number and approximate absolute amount
        var matches = allPostings
            .Where(p => p.AccountNumber == accountNumber
                     && Math.Abs(Math.Abs(p.AmountGross) - Math.Abs(amount)) < 1m)
            .ToList();

        if (!matches.Any())
        {
            _logger.LogWarning("No posting found: account={A} amount≈{Amt}", accountNumber, amount);
            return (null, null);
        }

        var posting = matches.First();

        if (!byVoucher.TryGetValue(posting.VoucherId, out var voucherPostings))
            return (posting, null);

        // Find counter-posting: different account, preferably opposite sign
        var others = voucherPostings
            .Where(p => p.PostingId != posting.PostingId && p.AccountNumber != accountNumber)
            .ToList();

        if (!others.Any())
        {
            _logger.LogWarning("No counter-posting for voucher {V}", posting.VoucherId);
            return (posting, null);
        }

        var sign = posting.AmountGross >= 0 ? 1m : -1m;
        // Prefer opposite-sign counter with largest absolute amount
        var safeOthers = others.Where(p => !RequiresLinkedEntity(p.AccountNumber)).ToList();

        var counter = safeOthers.Where(p => p.AmountGross * sign < 0)
                                .OrderByDescending(p => Math.Abs(p.AmountGross))
                                .FirstOrDefault()
                      ?? safeOthers.OrderByDescending(p => Math.Abs(p.AmountGross)).FirstOrDefault()
                      ?? others.Where(p => p.AmountGross * sign < 0)
                               .OrderByDescending(p => Math.Abs(p.AmountGross))
                               .FirstOrDefault()
                      ?? others.OrderByDescending(p => Math.Abs(p.AmountGross)).First();

        return (posting, counter.AccountId);
    }

    private async Task<long?> ResolveId(TripletexApiClient api, string accountNumber, Dictionary<string, long> cache)
    {
        if (cache.TryGetValue(accountNumber, out var id)) return id;

        var r = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["number"] = accountNumber,
            ["count"] = "1",
            ["fields"] = "id,number"
        });

        if (r.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
            {
                var aid = v.GetProperty("id").GetInt64();
                cache[accountNumber] = aid;
                return aid;
            }
        }

        _logger.LogWarning("Account {N} not found in chart of accounts", accountNumber);
        return null;
    }

    private async Task<long?> ResolveFallbackCounterId(TripletexApiClient api, string accountNumber, Dictionary<string, long> cache)
    {
        var preferred = accountNumber.StartsWith("6", StringComparison.Ordinal) || accountNumber.StartsWith("7", StringComparison.Ordinal)
            ? new[] { "1920", "2050" }
            : new[] { "1920", "2050" };

        foreach (var candidate in preferred)
        {
            var resolved = await ResolveId(api, candidate, cache);
            if (resolved.HasValue)
                return resolved.Value;
        }

        return null;
    }

    private static bool RequiresLinkedEntity(string? accountNumber)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
            return false;

        return accountNumber.StartsWith("15", StringComparison.Ordinal)
            || accountNumber.StartsWith("24", StringComparison.Ordinal);
    }

    private async Task LoadAccountMap(
        TripletexApiClient api,
        Dictionary<long, string> idToNumber,
        Dictionary<string, long> numberToId)
    {
        var r = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["count"] = "500",
            ["fields"] = "id,number"
        });

        if (!r.TryGetProperty("values", out var vals)) return;

        foreach (var v in vals.EnumerateArray())
        {
            if (!v.TryGetProperty("id", out var idProp)) continue;
            if (!v.TryGetProperty("number", out var numProp)) continue;
            var id = idProp.GetInt64();
            var num = numProp.ValueKind == JsonValueKind.String
                ? numProp.GetString()
                : numProp.ValueKind == JsonValueKind.Number
                    ? numProp.GetInt32().ToString()
                    : null;
            if (num != null)
            {
                idToNumber[id] = num;
                numberToId[num] = id;
            }
        }
    }

    private async Task<List<PostingInfo>> GetAllPostings(TripletexApiClient api, string dateFrom, string dateTo)
    {
        var result = await api.GetAsync("/ledger/posting", new Dictionary<string, string>
        {
            ["dateFrom"] = dateFrom,
            ["dateTo"] = dateTo,
            ["count"] = "200",
            ["fields"] = "id,voucher(id,date,description),account(id,number),amountGross,date"
        });

        var list = new List<PostingInfo>();
        if (!result.TryGetProperty("values", out var vals)) return list;

        foreach (var v in vals.EnumerateArray())
        {
            var p = new PostingInfo();
            if (v.TryGetProperty("id", out var pid)) p.PostingId = pid.GetInt64();
            if (v.TryGetProperty("amountGross", out var ag) && ag.ValueKind == JsonValueKind.Number)
                p.AmountGross = ag.GetDecimal();
            if (v.TryGetProperty("date", out var d)) p.Date = d.GetString() ?? "";
            if (v.TryGetProperty("voucher", out var vo) && vo.ValueKind == JsonValueKind.Object)
                if (vo.TryGetProperty("id", out var vid)) p.VoucherId = vid.GetInt64();
            if (v.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object)
            {
                if (acc.TryGetProperty("id", out var aid)) p.AccountId = aid.GetInt64();
                if (acc.TryGetProperty("number", out var anum))
                {
                    p.AccountNumber = anum.ValueKind == JsonValueKind.String
                        ? anum.GetString()
                        : anum.ValueKind == JsonValueKind.Number
                            ? anum.GetInt32().ToString()
                            : null;
                }
            }
            if (p.PostingId > 0) list.Add(p);
        }

        return list;
    }

    private List<CorrectionEntry> ParseCorrections(ExtractionResult extracted)
    {
        var list = new List<CorrectionEntry>();
        for (int i = 1; i <= 10; i++)
        {
            if (!extracted.Entities.TryGetValue($"correction{i}", out var entity)) break;
            var c = ParseCorrectionEntity(entity);
            if (c != null) list.Add(c);
        }
        // Singular "correction" fallback
        if (list.Count == 0 && extracted.Entities.TryGetValue("correction", out var single))
        {
            var c = ParseCorrectionEntity(single);
            if (c != null) list.Add(c);
        }
        return list;
    }

    private CorrectionEntry? ParseCorrectionEntity(Dictionary<string, object> e)
    {
        var errorType = GetStr(e, "errorType");
        var account = GetStr(e, "account");
        if (errorType == null || account == null) return null;
        return new CorrectionEntry
        {
            ErrorType = errorType,
            Account = account,
            CorrectAccount = GetStr(e, "correctAccount"),
            Amount = GetDec(e, "amount") ?? 0m,
            PostedAmount = GetDec(e, "postedAmount"),
            CorrectAmount = GetDec(e, "correctAmount"),
            VatAccount = GetStr(e, "vatAccount")
        };
    }

    private static string? GetStr(Dictionary<string, object> e, string k)
    {
        if (!e.TryGetValue(k, out var v)) return null;
        if (v is string s) return s;
        if (v is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : null;
        return v?.ToString();
    }

    private static decimal? GetDec(Dictionary<string, object> e, string k)
    {
        if (!e.TryGetValue(k, out var v)) return null;
        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetDecimal();
            if (je.ValueKind == JsonValueKind.String && decimal.TryParse(je.GetString(),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2))
                return d2;
        }
        if (decimal.TryParse(v?.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    private class PostingInfo
    {
        public long PostingId { get; set; }
        public long VoucherId { get; set; }
        public long AccountId { get; set; }
        public string? AccountNumber { get; set; }
        public decimal AmountGross { get; set; }
        public string Date { get; set; } = "";
    }

    private class CorrectionEntry
    {
        public string ErrorType { get; set; } = "";
        public string Account { get; set; } = "";
        public string? CorrectAccount { get; set; }
        public decimal Amount { get; set; }
        public decimal? PostedAmount { get; set; }
        public decimal? CorrectAmount { get; set; }
        public string? VatAccount { get; set; }
    }
}
