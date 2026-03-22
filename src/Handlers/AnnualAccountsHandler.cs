using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles Task 30: Simplified annual accounts (depreciation, prepaid reversal, tax calculation).
/// Creates 5 vouchers: 3 depreciation (one per asset), 1 prepaid reversal, 1 tax expense.
/// </summary>
public class AnnualAccountsHandler : ITaskHandler
{
    private readonly ILogger<AnnualAccountsHandler> _logger;

    public AnnualAccountsHandler(ILogger<AnnualAccountsHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var result = new HandlerResult { EntityType = "voucher" };
        var createdVoucherIds = new List<long>();

        // Parse extraction data
        var annualAccounts = extracted.Entities.GetValueOrDefault("annualAccounts") ?? new();
        var date = GetStringField(annualAccounts, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : $"{DateTime.Now.Year}-12-31");
        var year = date.Length >= 4 ? date[..4] : DateTime.Now.Year.ToString();

        // Extract account numbers (with defaults matching competition prompts)
        var depExpenseAccount = GetStringField(annualAccounts, "depreciationExpenseAccount") ?? "6010";
        var accumDepAccount = GetStringField(annualAccounts, "accumulatedDepreciationAccount") ?? "1209";
        var prepaidAccount = GetStringField(annualAccounts, "prepaidAccount") ?? "1700";
        var taxExpenseAccount = GetStringField(annualAccounts, "taxExpenseAccount") ?? "8700";
        var taxPayableAccount = GetStringField(annualAccounts, "taxPayableAccount") ?? "2920";
        var taxRate = GetDecimalField(annualAccounts, "taxRate") ?? 0.22m;

        // Extract prepaid amount
        var prepaidAmount = GetDecimalField(annualAccounts, "prepaidAmount") ?? 0m;

        // Extract assets from numbered entities (asset1, asset2, asset3, ...)
        var assets = new List<AssetInfo>();
        foreach (var (key, entity) in extracted.Entities)
        {
            if (!key.StartsWith("asset", StringComparison.OrdinalIgnoreCase)) continue;
            var name = GetStringField(entity, "name") ?? key;
            var bookValue = GetDecimalField(entity, "bookValue") ?? 0m;
            var usefulLife = GetDecimalField(entity, "usefulLife") ?? 0m;
            var assetAccount = GetStringField(entity, "assetAccount") ?? GetStringField(entity, "account") ?? "";
            if (bookValue > 0 && usefulLife > 0)
                assets.Add(new AssetInfo(name, bookValue, usefulLife, assetAccount));
        }

        // Also check for assets array inside annualAccounts entity
        if (assets.Count == 0 && annualAccounts.TryGetValue("assets", out var assetsVal) && assetsVal is JsonElement assetsArr && assetsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetsArr.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "Asset" : "Asset";
                var bookValue = item.TryGetProperty("bookValue", out var bv) ? ParseDecimal(bv) : 0m;
                var usefulLife = item.TryGetProperty("usefulLife", out var ul) ? ParseDecimal(ul) : 0m;
                var assetAccount = item.TryGetProperty("assetAccount", out var aa) ? aa.GetRawText().Trim('"') :
                    item.TryGetProperty("account", out var ac) ? ac.GetRawText().Trim('"') : "";
                if (bookValue > 0 && usefulLife > 0)
                    assets.Add(new AssetInfo(name, bookValue, usefulLife, assetAccount));
            }
        }

        // Extract provisions from numbered entities (provision1, provision2, journalEntry1, ...)
        var provisions = new List<ProvisionInfo>();
        foreach (var (key, entity) in extracted.Entities)
        {
            if (!key.StartsWith("provision", StringComparison.OrdinalIgnoreCase) &&
                !key.StartsWith("journalEntry", StringComparison.OrdinalIgnoreCase)) continue;
            var debitAccount = GetStringField(entity, "debitAccount") ?? GetStringField(entity, "expenseAccount") ?? "";
            var creditAccount = GetStringField(entity, "creditAccount") ?? GetStringField(entity, "liabilityAccount") ?? "";
            var amount = GetDecimalField(entity, "amount");
            if (!string.IsNullOrEmpty(debitAccount) && !string.IsNullOrEmpty(creditAccount))
                provisions.Add(new ProvisionInfo(key, debitAccount, creditAccount, amount));
        }

        _logger.LogInformation("Annual accounts: {AssetCount} assets, {ProvisionCount} provisions, prepaid={Prepaid}, taxRate={TaxRate}, date={Date}",
            assets.Count, provisions.Count, prepaidAmount, taxRate, date);

        // Step 1: Ensure required accounts exist (1209, 8700, depExpenseAccount are often missing)
        await EnsureAccountExists(api, accumDepAccount, "Akkumulerte avskrivninger", "Driftsmidler");
        await EnsureAccountExists(api, taxExpenseAccount, "Skattekostnad", "Skattekostnad");
        await EnsureAccountExists(api, depExpenseAccount, $"Avskrivningskostnad", "Driftskostnader");

        // Also ensure provision accounts exist (5000, 2900 may be missing in some environments)
        foreach (var prov in provisions)
        {
            if (!string.IsNullOrEmpty(prov.DebitAccount))
                await EnsureAccountExists(api, prov.DebitAccount, $"Konto {prov.DebitAccount}", "Driftskostnader");
            if (!string.IsNullOrEmpty(prov.CreditAccount))
                await EnsureAccountExists(api, prov.CreditAccount, $"Konto {prov.CreditAccount}", "Kortsiktig gjeld");
        }

        // Step 2: Resolve all account IDs
        var accountCache = new Dictionary<string, long>();
        var accountsToResolve = new HashSet<string> { depExpenseAccount, accumDepAccount, prepaidAccount, taxExpenseAccount, taxPayableAccount };
        foreach (var asset in assets)
            if (!string.IsNullOrEmpty(asset.AssetAccount))
                accountsToResolve.Add(asset.AssetAccount);
        foreach (var prov in provisions)
        {
            if (!string.IsNullOrEmpty(prov.DebitAccount)) accountsToResolve.Add(prov.DebitAccount);
            if (!string.IsNullOrEmpty(prov.CreditAccount)) accountsToResolve.Add(prov.CreditAccount);
        }

        foreach (var acctNum in accountsToResolve)
        {
            var (id, _, _) = await ResolveAccountId(api, acctNum);
            if (id.HasValue)
                accountCache[acctNum] = id.Value;
            else
                _logger.LogWarning("Account {Account} not found — voucher creation may fail", acctNum);
        }

        // Step 2.5: Query existing P&L BEFORE posting our vouchers (for accurate tax computation)
        double existingPnLSum = 0;
        {
            var pnlYear = date.Length >= 4 ? date[..4] : DateTime.Now.Year.ToString();
            var pnlDateFrom = $"{pnlYear}-01-01";
            var pnlDateTo = $"{int.Parse(pnlYear) + 1}-01-01";
            var pnlResult = await api.GetAsync("/ledger/posting", new Dictionary<string, string>
            {
                ["dateFrom"] = pnlDateFrom,
                ["dateTo"] = pnlDateTo,
                ["accountNumberFrom"] = "3000",
                ["accountNumberTo"] = "8699",
                ["count"] = "10000",
                ["fields"] = "amount"
            });
            if (pnlResult.TryGetProperty("values", out var pnlValues))
            {
                foreach (var v in pnlValues.EnumerateArray())
                {
                    if (v.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number)
                        existingPnLSum += amt.GetDouble();
                }
            }
            _logger.LogInformation("Pre-existing P&L sum (before our adjustments): {Sum:F2}", existingPnLSum);
        }

        // Step 3: Create depreciation vouchers (one per asset)
        // Detect if year-end (December 31) → annual depreciation, otherwise → monthly
        var isYearEnd = date.EndsWith("-12-31");
        foreach (var asset in assets)
        {
            var depreciation = isYearEnd
                ? Math.Round(asset.BookValue / asset.UsefulLife, 2)          // Annual
                : Math.Round(asset.BookValue / asset.UsefulLife / 12m, 2);   // Monthly
            var description = $"Avskrivning {asset.Name}";

            _logger.LogInformation("{Period} depreciation for {Name}: {BookValue} / {Life} years{Monthly} = {Depreciation}",
                isYearEnd ? "Annual" : "Monthly", asset.Name, asset.BookValue, asset.UsefulLife,
                isYearEnd ? "" : " / 12", depreciation);

            if (!accountCache.TryGetValue(depExpenseAccount, out var depExpId) ||
                !accountCache.TryGetValue(accumDepAccount, out var accumDepId))
            {
                _logger.LogWarning("Missing account IDs for depreciation voucher — skipping {Name}", asset.Name);
                continue;
            }

            var postings = new[]
            {
                new Dictionary<string, object>
                {
                    ["date"] = date,
                    ["description"] = description,
                    ["account"] = new { id = depExpId },
                    ["amountGross"] = (double)depreciation,
                    ["amountGrossCurrency"] = (double)depreciation,
                    ["row"] = 1
                },
                new Dictionary<string, object>
                {
                    ["date"] = date,
                    ["description"] = description,
                    ["account"] = new { id = accumDepId },
                    ["amountGross"] = (double)(-depreciation),
                    ["amountGrossCurrency"] = (double)(-depreciation),
                    ["row"] = 2
                }
            };

            var voucherId = await CreateVoucher(api, date, description, postings);
            if (voucherId > 0)
            {
                createdVoucherIds.Add(voucherId);
                _logger.LogInformation("Created depreciation voucher for {Name}: ID={Id}, amount={Amount}", asset.Name, voucherId, depreciation);
            }
        }

        // Step 4: Prepaid expense reversal
        if (prepaidAmount > 0 && accountCache.TryGetValue(prepaidAccount, out var prepaidId))
        {
            // Determine the counter-account for prepaid reversal
            // The prompt says "reverser forskuddsbetalte kostnader" — we credit 1700 and debit an expense account
            // Use a generic operating expense account (6800 or similar), but the standard approach
            // is to just zero out the prepaid account by reversing it against a cost account.
            // Looking at competition patterns: debit 6800 (andre driftskostnader), credit 1700
            var prepaidCounterAccount = GetStringField(annualAccounts, "prepaidCounterAccount") ?? "6800";
            if (!accountCache.ContainsKey(prepaidCounterAccount))
            {
                var (cid, _, _) = await ResolveAccountId(api, prepaidCounterAccount);
                if (cid.HasValue) accountCache[prepaidCounterAccount] = cid.Value;
            }

            if (accountCache.TryGetValue(prepaidCounterAccount, out var counterAcctId))
            {
                var description = "Reversering forskuddsbetalte kostnader";
                var postings = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = counterAcctId },
                        ["amountGross"] = (double)prepaidAmount,
                        ["amountGrossCurrency"] = (double)prepaidAmount,
                        ["row"] = 1
                    },
                    new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = prepaidId },
                        ["amountGross"] = (double)(-prepaidAmount),
                        ["amountGrossCurrency"] = (double)(-prepaidAmount),
                        ["row"] = 2
                    }
                };

                var voucherId = await CreateVoucher(api, date, description, postings);
                if (voucherId > 0)
                {
                    createdVoucherIds.Add(voucherId);
                    _logger.LogInformation("Created prepaid reversal voucher: ID={Id}, amount={Amount}", voucherId, prepaidAmount);
                }
            }
        }

        // Step 4.5: Provision vouchers (salary provisions, etc.)
        foreach (var prov in provisions)
        {
            var provAmount = (prov.Amount.HasValue && prov.Amount.Value > 0) ? prov.Amount.Value : prepaidAmount; // fallback to prepaidAmount
            if (provAmount <= 0)
            {
                _logger.LogWarning("Provision {Key} has no amount and no fallback — skipping", prov.Key);
                continue;
            }

            if (!accountCache.TryGetValue(prov.DebitAccount, out var provDebitId) ||
                !accountCache.TryGetValue(prov.CreditAccount, out var provCreditId))
            {
                _logger.LogWarning("Missing account IDs for provision {Key} (debit={Debit}, credit={Credit}) — skipping",
                    prov.Key, prov.DebitAccount, prov.CreditAccount);
                continue;
            }

            var description = $"Avsetning lønn";
            var postings = new[]
            {
                new Dictionary<string, object>
                {
                    ["date"] = date,
                    ["description"] = description,
                    ["account"] = new { id = provDebitId },
                    ["amountGross"] = (double)provAmount,
                    ["amountGrossCurrency"] = (double)provAmount,
                    ["row"] = 1
                },
                new Dictionary<string, object>
                {
                    ["date"] = date,
                    ["description"] = description,
                    ["account"] = new { id = provCreditId },
                    ["amountGross"] = (double)(-provAmount),
                    ["amountGrossCurrency"] = (double)(-provAmount),
                    ["row"] = 2
                }
            };

            var voucherId = await CreateVoucher(api, date, description, postings);
            if (voucherId > 0)
            {
                createdVoucherIds.Add(voucherId);
                _logger.LogInformation("Created provision voucher {Key}: ID={Id}, amount={Amount}, debit={Debit}, credit={Credit}",
                    prov.Key, voucherId, provAmount, prov.DebitAccount, prov.CreditAccount);
            }
        }

        // Step 5: Tax calculation and voucher (analytical — based on pre-existing P&L + our known expenses)
        if (accountCache.TryGetValue(taxExpenseAccount, out var taxExpId) &&
            accountCache.TryGetValue(taxPayableAccount, out var taxPayId))
        {
            // Calculate total expenses we're adding to P&L accounts (3000-8699 range)
            double totalDepreciation = 0;
            foreach (var asset in assets)
            {
                var dep = isYearEnd
                    ? (double)Math.Round(asset.BookValue / asset.UsefulLife, 2)
                    : (double)Math.Round(asset.BookValue / asset.UsefulLife / 12m, 2);
                totalDepreciation += dep;
            }
            double prepaidExpense = (double)prepaidAmount; // goes to 6800 (in P&L range)
            double provisionExpense = 0;
            foreach (var prov in provisions)
            {
                // Only count provisions whose debit account is in P&L range (3000-8699)
                if (int.TryParse(prov.DebitAccount, out var debitNum) && debitNum >= 3000 && debitNum <= 8699)
                {
                    var provAmt = (prov.Amount.HasValue && prov.Amount.Value > 0) ? prov.Amount.Value : prepaidAmount;
                    provisionExpense += (double)provAmt;
                }
            }

            double ourAdditionalExpenses = totalDepreciation + prepaidExpense + provisionExpense;
            double adjustedPnL = existingPnLSum + ourAdditionalExpenses;
            double taxableResult = -adjustedPnL; // negative P&L sum = income > expense = profit
            double taxAmount = taxableResult > 0 ? Math.Round(taxableResult * (double)taxRate, 2) : 0;

            _logger.LogInformation("Tax analytical: existingPnL={Existing:F2}, ourExpenses={Ours:F2} (dep={Dep:F2}+prepaid={Pre:F2}+prov={Prov:F2}), adjusted={Adj:F2}, taxable={Tax:F2}, rate={Rate}%, tax={TaxAmt:F2}",
                existingPnLSum, ourAdditionalExpenses, totalDepreciation, prepaidExpense, provisionExpense,
                adjustedPnL, taxableResult, (double)taxRate * 100, taxAmount);

            if (taxAmount > 0)
            {
                var description = "Skattekostnad";
                var postings = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = taxExpId },
                        ["amountGross"] = taxAmount,
                        ["amountGrossCurrency"] = taxAmount,
                        ["row"] = 1
                    },
                    new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = taxPayId },
                        ["amountGross"] = -taxAmount,
                        ["amountGrossCurrency"] = -taxAmount,
                        ["row"] = 2
                    }
                };

                var voucherId = await CreateVoucher(api, date, description, postings);
                if (voucherId > 0)
                {
                    createdVoucherIds.Add(voucherId);
                    _logger.LogInformation("Created tax voucher: ID={Id}, taxAmount={Amount}", voucherId, taxAmount);
                }
            }
            else
            {
                _logger.LogInformation("Tax amount is 0 or negative — skipping tax voucher (no taxable profit)");
            }
        }

        _logger.LogInformation("Annual accounts complete: {Count} vouchers created", createdVoucherIds.Count);

        if (createdVoucherIds.Count > 0)
        {
            result.EntityId = createdVoucherIds[0];
            foreach (var id in createdVoucherIds.Skip(1))
                result.AdditionalEntityIds.Add(id);
        }
        result.Metadata["voucherCount"] = createdVoucherIds.Count.ToString();

        return result;
    }

    private async Task<long> CreateVoucher(TripletexApiClient api, string date, string description, Dictionary<string, object>[] postings)
    {
        var body = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["postings"] = postings
        };

        var voucherResult = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
        return voucherResult.GetProperty("value").GetProperty("id").GetInt64();
    }

    private async Task EnsureAccountExists(TripletexApiClient api, string accountNumber, string name, string description)
    {
        var existing = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["number"] = accountNumber,
            ["count"] = "1",
            ["fields"] = "id,number"
        });

        if (existing.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
        {
            _logger.LogInformation("Account {Number} already exists", accountNumber);
            return;
        }

        // Account doesn't exist — create it
        var acctNum = int.Parse(accountNumber);
        // Determine account type based on number range
        // 1xxx = Assets, 2xxx = Liabilities, 3xxx-8xxx = Income/Expense
        var body = new Dictionary<string, object>
        {
            ["number"] = acctNum,
            ["name"] = name,
            ["description"] = description
        };

        try
        {
            await api.PostAsync("/ledger/account", body);
            _logger.LogInformation("Created account {Number} ({Name})", accountNumber, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create account {Number}: {Error}", accountNumber, ex.Message);
        }
    }

    private async Task<(long? accountId, long? vatTypeId, bool vatLocked)> ResolveAccountId(TripletexApiClient api, string accountNumber)
    {
        var result = await api.GetAsync("/ledger/account", new Dictionary<string, string>
        {
            ["number"] = accountNumber,
            ["count"] = "1",
            ["fields"] = "id,number,vatLocked,vatType(id,number)"
        });
        if (result.TryGetProperty("values", out var vals))
        {
            foreach (var v in vals.EnumerateArray())
            {
                var id = v.GetProperty("id").GetInt64();
                long? vatId = null;
                bool locked = v.TryGetProperty("vatLocked", out var vl) && vl.ValueKind == JsonValueKind.True;
                if (v.TryGetProperty("vatType", out var vt) && vt.ValueKind == JsonValueKind.Object)
                {
                    if (vt.TryGetProperty("id", out var vtId) && vtId.ValueKind == JsonValueKind.Number)
                    {
                        var rawId = vtId.GetInt64();
                        int vatNumber = 0;
                        if (vt.TryGetProperty("number", out var vtNum) && vtNum.ValueKind == JsonValueKind.Number)
                            vatNumber = vtNum.GetInt32();
                        if (locked && vatNumber != 0 && rawId > 0)
                            vatId = rawId;
                    }
                }
                return (id, vatId, locked);
            }
        }
        _logger.LogWarning("Account {Number} not found", accountNumber);
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

    private static decimal ParseDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return 0m;
    }

    private record AssetInfo(string Name, decimal BookValue, decimal UsefulLife, string AssetAccount);
    private record ProvisionInfo(string Key, string DebitAccount, string CreditAccount, decimal? Amount);
}
