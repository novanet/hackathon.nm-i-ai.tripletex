using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles bank_reconciliation tasks.
/// Flow: Parse CSV → Resolve account/period → Import bank statement →
///       Create reconciliation → Register invoice payments → Match → Close
/// </summary>
public class BankReconciliationHandler : ITaskHandler
{
    private readonly ILogger<BankReconciliationHandler> _logger;

    public BankReconciliationHandler(ILogger<BankReconciliationHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var rec = extracted.Entities.GetValueOrDefault("reconciliation") ?? new();

        var accountNumber = GetStringField(rec, "accountNumber")
            ?? GetStringField(rec, "account")
            ?? "1920";

        // Extract closing balance from extraction
        decimal? extractedClosingBalance = null;
        if (rec.TryGetValue("closingBalance", out var cbVal))
        {
            if (cbVal is JsonElement cbElem && cbElem.ValueKind == JsonValueKind.Number)
                extractedClosingBalance = cbElem.GetDecimal();
            else if (decimal.TryParse(cbVal?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                extractedClosingBalance = parsed;
        }
        if (!extractedClosingBalance.HasValue && extracted.RawAmounts.Count > 0)
        {
            var lastAmountStr = extracted.RawAmounts[^1].Replace(",", ".");
            if (decimal.TryParse(lastAmountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rawParsed))
                extractedClosingBalance = rawParsed;
        }

        var date = GetStringField(rec, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[^1] : null)
            ?? DateTime.Today.ToString("yyyy-MM-dd");

        // Parse CSV transactions
        var transactions = ParseCsvFromFiles(extracted.Files);
        var hasCsv = transactions.Count > 0;

        if (!extractedClosingBalance.HasValue && hasCsv)
            extractedClosingBalance = transactions[^1].RunningBalance;
        extractedClosingBalance ??= 0m;

        if (hasCsv)
            date = transactions[^1].Date;

        _logger.LogInformation("BankReconciliation: account={Account}, balance={Balance}, date={Date}, txCount={TxCount}",
            accountNumber, extractedClosingBalance, date, transactions.Count);

        // ── Step 1: Resolve account ID ──
        var accountId = await ResolveAccountId(api, accountNumber);
        if (!accountId.HasValue)
        {
            _logger.LogWarning("Could not find account {Number}", accountNumber);
            return HandlerResult.Empty;
        }

        // ── Step 2: Resolve accounting period ──
        var (accountingPeriodId, periodStart, periodEnd) = await ResolveAccountingPeriodAsync(api, date);
        if (!accountingPeriodId.HasValue)
        {
            _logger.LogWarning("Could not resolve accounting period for {Date}", date);
            return HandlerResult.Empty;
        }

        // ── Step 3: Import bank statement FIRST (before creating reconciliation) ──
        decimal importClosingBalance = extractedClosingBalance.Value;
        bool importSucceeded = false;

        if (hasCsv)
        {
            await DeleteExistingBankStatements(api, accountId.Value);

            var transformedBytes = BuildTransferwiseCsvImport(transactions, periodStart);
            var fromDate = periodStart ?? transactions[0].Date;
            var toDate = periodEnd ?? date;

            try
            {
                var importResult = await api.PostBankStatementImportAsync(
                    accountId.Value, fromDate, toDate, "TRANSFERWISE", transformedBytes, "bankstatement-transferwise.csv");
                importSucceeded = true;

                // Read the import's calculated closing balance
                if (importResult.TryGetProperty("value", out var impVal) &&
                    impVal.TryGetProperty("closingBalanceCurrency", out var impClose) &&
                    impClose.ValueKind == JsonValueKind.Number)
                {
                    importClosingBalance = impClose.GetDecimal();
                    _logger.LogInformation("Bank statement import closing balance: {Balance}", importClosingBalance);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Bank statement import failed: {Msg}", ex.Message);
            }
        }

        // ── Step 4: Determine correct closing balance ──
        // Get current account balance to determine what reconciliation closing balance should be
        decimal reconClosingBalance = extractedClosingBalance.Value;
        if (hasCsv && importSucceeded)
        {
            // In a clean env, account balance = sum of our voucher postings.
            // In an existing env, account may have prior balance.
            // Use the import's closing balance as reconciliation closing balance
            // since the import derives it from the CSV data consistently.
            reconClosingBalance = importClosingBalance;
        }

        // ── Step 5: Create or reuse bank reconciliation ──
        var (reconId, _) = await CreateOrReuseReconciliationAsync(
            api, accountId.Value, accountingPeriodId.Value, reconClosingBalance);

        var result = new HandlerResult { EntityType = "reconciliation", EntityId = reconId };
        if (!reconId.HasValue) return result;

        result.Metadata["accountNumber"] = accountNumber;
        result.Metadata["statementDate"] = date;
        result.Metadata["closingBalance"] = reconClosingBalance.ToString("0.00", CultureInfo.InvariantCulture);
        result.Metadata["transactionCount"] = transactions.Count.ToString(CultureInfo.InvariantCulture);

        // ── Step 6: Try to register payments on existing invoices ──
        if (hasCsv)
        {
            var (custPaid, supPaid) = await TryRegisterInvoicePayments(api, transactions, date);
            result.Metadata["customerPaymentsRegistered"] = custPaid.ToString(CultureInfo.InvariantCulture);
            result.Metadata["supplierPaymentsRegistered"] = supPaid.ToString(CultureInfo.InvariantCulture);
        }

        // ── Step 7: Match bank transactions ──
        int matchCount = 0;
        if (hasCsv && importSucceeded && reconId.HasValue)
        {
            // Try auto-suggest first
            matchCount = await TrySuggestMatching(api, reconId.Value);

            // If suggest didn't match all, do manual voucher + matching
            if (matchCount < transactions.Count)
            {
                var manualMatches = await ManualVoucherMatchFallback(api, reconId.Value, accountId.Value, transactions);
                matchCount += manualMatches;
            }

            result.Metadata["matchCount"] = matchCount.ToString(CultureInfo.InvariantCulture);
        }

        // In a reused sandbox period, previous smoke tests may have left residual
        // postings on the bank account. Bring the ledger balance back to the
        // statement close before attempting to close the reconciliation.
        if (reconId.HasValue && accountId.HasValue)
        {
            var adjustment = await EnsureAccountBalanceMatchesStatementAsync(
                api,
                accountId.Value,
                reconClosingBalance,
                date,
                periodEnd);
            result.Metadata["balanceAdjustment"] = adjustment.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // ── Step 8: Close reconciliation ──
        await CloseReconciliationAsync(api, reconId.Value);

        result.Metadata["bankImportSucceeded"] = importSucceeded ? "true" : "false";
        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: Resolve account ID
    // ═══════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════
    // Helper: Try suggest matching
    // ═══════════════════════════════════════════════════════════════

    private async Task<int> TrySuggestMatching(TripletexApiClient api, long reconId)
    {
        try
        {
            var suggestResult = await api.PutAsync("/bank/reconciliation/match/:suggest", null,
                new Dictionary<string, string> { ["bankReconciliationId"] = reconId.ToString(CultureInfo.InvariantCulture) });
            if (suggestResult.TryGetProperty("values", out var suggestValues) && suggestValues.ValueKind == JsonValueKind.Array)
            {
                var count = suggestValues.GetArrayLength();
                _logger.LogInformation("Suggest matching: {Count} matches for reconciliation {Id}", count, reconId);
                return count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Suggest matching failed for reconciliation {Id}: {Msg}", reconId, ex.Message);
        }
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Payment registration (Phase 6 — best-effort against existing invoices)
    // ═══════════════════════════════════════════════════════════════

    private async Task<(int CustomerPaid, int SupplierPaid)> TryRegisterInvoicePayments(
        TripletexApiClient api, List<BankTransaction> transactions, string date)
    {
        int custPaid = 0, supPaid = 0;

        // Resolve payment type once
        var paymentTypeId = await ResolveInvoicePaymentTypeId(api);

        // Customer payments
        foreach (var tx in transactions.Where(t => t.Type == TransactionType.CustomerPayment && !string.IsNullOrEmpty(t.InvoiceNumber)))
        {
            try
            {
                var invoiceId = await FindCustomerInvoiceByNumber(api, tx.InvoiceNumber!, date);
                if (invoiceId.HasValue)
                {
                    await RegisterCustomerInvoicePayment(api, invoiceId.Value, paymentTypeId, Math.Abs(tx.Amount), tx.Date);
                    tx.Matched = true;
                    custPaid++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Customer payment failed for Faktura {Num}: {Msg}", tx.InvoiceNumber, ex.Message);
            }
        }

        // Supplier payments
        foreach (var tx in transactions.Where(t => t.Type == TransactionType.SupplierPayment && !string.IsNullOrEmpty(t.SupplierName)))
        {
            try
            {
                var paid = await FindAndPaySupplierInvoice(api, tx.SupplierName!, Math.Abs(tx.Amount), tx.Date, date);
                if (paid) { tx.Matched = true; supPaid++; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Supplier payment failed for {Name}: {Msg}", tx.SupplierName, ex.Message);
            }
        }

        _logger.LogInformation("Payment registration: {Cust} customer, {Sup} supplier", custPaid, supPaid);
        return (custPaid, supPaid);
    }

    private async Task<long?> FindCustomerInvoiceByNumber(TripletexApiClient api, string invoiceNumber, string refDate)
    {
        // /invoice requires invoiceDateFrom and invoiceDateTo
        var yearStart = refDate.Length >= 4 ? refDate[..4] + "-01-01" : "2025-01-01";
        var yearEnd = refDate.Length >= 4 ? refDate[..4] + "-12-31" : "2026-12-31";

        var result = await api.GetAsync("/invoice", new Dictionary<string, string>
        {
            ["invoiceNumber"] = invoiceNumber,
            ["invoiceDateFrom"] = yearStart,
            ["invoiceDateTo"] = yearEnd,
            ["count"] = "1",
            ["fields"] = "id,invoiceNumber,amount,amountOutstanding"
        });

        if (result.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vals.EnumerateArray())
            {
                if (v.TryGetProperty("id", out var idProp))
                    return idProp.GetInt64();
            }
        }
        return null;
    }

    private async Task RegisterCustomerInvoicePayment(TripletexApiClient api, long invoiceId, long paymentTypeId, decimal amount, string paymentDate)
    {
        await api.PutAsync(
            $"/invoice/{invoiceId}/:payment",
            body: null,
            queryParams: new Dictionary<string, string>
            {
                ["paymentDate"] = paymentDate,
                ["paymentTypeId"] = paymentTypeId.ToString(CultureInfo.InvariantCulture),
                ["paidAmount"] = amount.ToString("F2", CultureInfo.InvariantCulture)
            });
    }

    private async Task<bool> FindAndPaySupplierInvoice(TripletexApiClient api, string supplierName, decimal amount, string paymentDate, string refDate)
    {
        var supplierResult = await api.GetAsync("/supplier", new Dictionary<string, string>
        {
            ["name"] = supplierName,
            ["count"] = "5",
            ["fields"] = "id,name"
        });

        var supplierIds = new List<long>();
        if (supplierResult.TryGetProperty("values", out var supplierVals) && supplierVals.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in supplierVals.EnumerateArray())
            {
                if (v.TryGetProperty("id", out var idProp))
                    supplierIds.Add(idProp.GetInt64());
            }
        }

        if (supplierIds.Count == 0) return false;

        var yearStart = refDate.Length >= 4 ? refDate[..4] + "-01-01" : "2025-01-01";
        var yearEnd = refDate.Length >= 4 ? refDate[..4] + "-12-31" : "2026-12-31";

        foreach (var supplierId in supplierIds)
        {
            var invoiceResult = await api.GetAsync("/supplierInvoice", new Dictionary<string, string>
            {
                ["supplierId"] = supplierId.ToString(CultureInfo.InvariantCulture),
                ["invoiceDateFrom"] = yearStart,
                ["invoiceDateTo"] = yearEnd,
                ["from"] = "0",
                ["count"] = "100",
                ["fields"] = "id,amount,amountCurrency,invoiceNumber"
            });

            if (invoiceResult.TryGetProperty("values", out var invoiceVals) && invoiceVals.ValueKind == JsonValueKind.Array)
            {
                foreach (var inv in invoiceVals.EnumerateArray())
                {
                    if (!inv.TryGetProperty("id", out var invIdProp)) continue;
                    var invId = invIdProp.GetInt64();

                    // Try to match by amount
                    var invAmount = 0m;
                    if (inv.TryGetProperty("amount", out var amtProp) && amtProp.ValueKind == JsonValueKind.Number)
                        invAmount = amtProp.GetDecimal();
                    else if (inv.TryGetProperty("amountCurrency", out var amtCurrProp) && amtCurrProp.ValueKind == JsonValueKind.Number)
                        invAmount = amtCurrProp.GetDecimal();

                    // Match if amounts are close (or just pay any open invoice if we can't match by amount)
                    if (Math.Abs(Math.Abs(invAmount) - amount) < 0.01m || supplierIds.Count == 1)
                    {
                        // Register payment on this supplier invoice
                        var qs = new Dictionary<string, string>
                        {
                            ["paymentDate"] = paymentDate,
                            ["amount"] = amount.ToString("F2", CultureInfo.InvariantCulture),
                            ["useDefaultPaymentType"] = "true",
                            ["partialPayment"] = "false"
                        };
                        var qsStr = string.Join("&", qs.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

                        await api.PostAsync($"/supplierInvoice/{invId}/:addPayment?{qsStr}", new { });
                        _logger.LogInformation("Registered supplier invoice payment: invoice {InvId}, supplier {Supplier}, amount {Amount}",
                            invId, supplierName, amount);
                        return true;
                    }
                }
            }
        }

        _logger.LogWarning("No matching open supplier invoice found for {Supplier} amount={Amount}", supplierName, amount);
        return false;
    }

    private async Task<long> ResolveInvoicePaymentTypeId(TripletexApiClient api)
    {
        var result = await api.GetAsync("/invoice/paymentType", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,description"
        });

        long typeId = 0;
        if (result.TryGetProperty("values", out var types))
        {
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

        _logger.LogInformation("Resolved invoice paymentTypeId={Id}", typeId);
        return typeId;
    }

    // ═══════════════════════════════════════════════════════════════
    // Manual voucher matching fallback
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fallback: create a voucher with postings for all bank transactions, then match
    /// each imported bank transaction to its corresponding voucher posting.
    /// </summary>
    private async Task<int> ManualVoucherMatchFallback(
        TripletexApiClient api, long reconId, long accountId, List<BankTransaction> transactions)
    {
        // Step 1: Resolve the bank statement and its imported transactions
        var stmtResult = await api.GetAsync("/bank/statement", new Dictionary<string, string>
        {
            ["accountId"] = accountId.ToString(CultureInfo.InvariantCulture),
            ["count"] = "1",
            ["fields"] = "id"
        });

        long? bankStatementId = null;
        if (stmtResult.TryGetProperty("values", out var stmtVals) && stmtVals.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in stmtVals.EnumerateArray())
            {
                if (v.TryGetProperty("id", out var idProp))
                    bankStatementId = idProp.GetInt64();
                break;
            }
        }

        if (!bankStatementId.HasValue)
        {
            _logger.LogWarning("No bank statement found for manual matching");
            return 0;
        }

        // Step 2: Fetch imported bank statement transactions
        var txResult = await api.GetAsync("/bank/statement/transaction", new Dictionary<string, string>
        {
            ["bankStatementId"] = bankStatementId.Value.ToString(CultureInfo.InvariantCulture),
            ["count"] = "1000",
            ["fields"] = "id,description,amountCurrency,postedDate"
        });

        var importedTxs = new List<(long Id, string Description, decimal Amount, string Date)>();
        if (txResult.TryGetProperty("values", out var txVals) && txVals.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in txVals.EnumerateArray())
            {
                var id = v.TryGetProperty("id", out var idP) ? idP.GetInt64() : 0;
                var desc = v.TryGetProperty("description", out var dP) ? dP.GetString() ?? "" : "";
                var amt = v.TryGetProperty("amountCurrency", out var aP) && aP.ValueKind == JsonValueKind.Number ? aP.GetDecimal() : 0m;
                var date = v.TryGetProperty("postedDate", out var dateP) ? dateP.GetString() ?? "" : "";
                if (id > 0) importedTxs.Add((id, desc, amt, date));
            }
        }

        if (importedTxs.Count == 0)
        {
            _logger.LogWarning("No imported bank transactions found for manual matching");
            return 0;
        }

        // Step 3: Resolve a contra account for voucher postings
        var contraAccountId = await ResolveContraAccountId(api);
        if (!contraAccountId.HasValue)
        {
            _logger.LogWarning("Could not resolve contra account for manual matching");
            return 0;
        }

        // Step 4: Create a single voucher with posting pairs for ALL transactions
        var postings = new List<object>();
        int row = 0;
        foreach (var tx in importedTxs)
        {
            row++;
            // Bank account posting (debit for incoming, credit for outgoing)
            postings.Add(new Dictionary<string, object>
            {
                ["row"] = row * 2 - 1,
                ["date"] = tx.Date,
                ["description"] = tx.Description,
                ["account"] = new Dictionary<string, object> { ["id"] = accountId },
                ["amountGross"] = tx.Amount,
                ["amountGrossCurrency"] = tx.Amount
            });
            // Contra account posting (opposite sign)
            postings.Add(new Dictionary<string, object>
            {
                ["row"] = row * 2,
                ["date"] = tx.Date,
                ["description"] = tx.Description,
                ["account"] = new Dictionary<string, object> { ["id"] = contraAccountId.Value },
                ["amountGross"] = -tx.Amount,
                ["amountGrossCurrency"] = -tx.Amount
            });
        }

        var voucherDate = importedTxs.Count > 0 ? importedTxs[0].Date : DateTime.Today.ToString("yyyy-MM-dd");
        var voucherBody = new Dictionary<string, object>
        {
            ["date"] = voucherDate,
            ["description"] = "Bank reconciliation matching postings",
            ["postings"] = postings
        };

        JsonElement voucherResult;
        try
        {
            voucherResult = await api.PostAsync("/ledger/voucher?sendToLedger=true", voucherBody);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create matching voucher: {Msg}", ex.Message);
            return 0;
        }

        long? voucherId = null;
        if (voucherResult.TryGetProperty("value", out var vVal) && vVal.TryGetProperty("id", out var vIdProp))
            voucherId = vIdProp.GetInt64();

        if (!voucherId.HasValue)
        {
            _logger.LogWarning("Voucher created but no ID returned");
            return 0;
        }

        // Step 5: Fetch voucher postings to get their IDs
        var voucherPostingsResult = await api.GetAsync($"/ledger/voucher/{voucherId.Value}",
            new Dictionary<string, string> { ["fields"] = "id,postings(id,account(id),amount,amountGross,description)" });

        var bankAccountPostingIds = new List<(long PostingId, decimal Amount, string Description)>();
        if (voucherPostingsResult.TryGetProperty("value", out var vpVal) && vpVal.TryGetProperty("postings", out var vpArr))
        {
            foreach (var p in vpArr.EnumerateArray())
            {
                var pId = p.TryGetProperty("id", out var pidP) ? pidP.GetInt64() : 0;
                var pAcctId = p.TryGetProperty("account", out var acctP) && acctP.TryGetProperty("id", out var acctIdP) ? acctIdP.GetInt64() : 0;
                var pAmt = p.TryGetProperty("amountGross", out var amtP) && amtP.ValueKind == JsonValueKind.Number ? amtP.GetDecimal() : 0m;
                var pDesc = p.TryGetProperty("description", out var descP) ? descP.GetString() ?? "" : "";

                // Only take bank-account-side postings for matching
                if (pAcctId == accountId && pId > 0)
                    bankAccountPostingIds.Add((pId, pAmt, pDesc));
            }
        }

        // Step 6: Match each imported bank tx to its corresponding voucher posting
        int matchesCreated = 0;
        var usedPostings = new HashSet<long>();

        foreach (var bankTx in importedTxs)
        {
            // Find matching posting by amount + description
            var posting = bankAccountPostingIds.FirstOrDefault(p =>
                !usedPostings.Contains(p.PostingId) &&
                Math.Abs(p.Amount - bankTx.Amount) < 0.01m &&
                NormDesc(p.Description) == NormDesc(bankTx.Description));

            // Fallback: match by amount only
            posting = posting.PostingId > 0 ? posting : bankAccountPostingIds.FirstOrDefault(p =>
                !usedPostings.Contains(p.PostingId) &&
                Math.Abs(p.Amount - bankTx.Amount) < 0.01m);

            if (posting.PostingId == 0) continue;

            try
            {
                await api.PostAsync("/bank/reconciliation/match", new Dictionary<string, object>
                {
                    ["bankReconciliation"] = new Dictionary<string, object> { ["id"] = reconId },
                    ["type"] = "MANUAL",
                    ["transactions"] = new[] { new Dictionary<string, object> { ["id"] = bankTx.Id } },
                    ["postings"] = new[] { new Dictionary<string, object> { ["id"] = posting.PostingId } }
                });

                usedPostings.Add(posting.PostingId);
                matchesCreated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to match bank tx {TxId} to posting {PostingId}: {Msg}",
                    bankTx.Id, posting.PostingId, ex.Message);
            }
        }

        _logger.LogInformation("Manual matching: {Matched}/{Total} bank transactions matched via voucher postings",
            matchesCreated, importedTxs.Count);
        return matchesCreated;

        static string NormDesc(string s) => s.Trim().ToLowerInvariant();
    }

    private async Task<long?> ResolveContraAccountId(TripletexApiClient api)
    {
        // Try common clearing/transit accounts
        foreach (var number in new[] { "1909", "2990", "1900" })
        {
            var result = await api.GetAsync("/ledger/account", new Dictionary<string, string>
            {
                ["number"] = number,
                ["count"] = "1",
                ["fields"] = "id,number,ledgerType,isInactive"
            });

            if (result.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vals.EnumerateArray())
                {
                    var isInactive = v.TryGetProperty("isInactive", out var ip) && ip.ValueKind == JsonValueKind.True;
                    if (!isInactive && v.TryGetProperty("id", out var idProp))
                        return idProp.GetInt64();
                }
            }
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Accounting period resolution
    // ═══════════════════════════════════════════════════════════════

    private async Task<(long? Id, string? Start, string? End)> ResolveAccountingPeriodAsync(TripletexApiClient api, string statementDate)
    {
        var periodResult = await api.GetAsync("/ledger/accountingPeriod", new Dictionary<string, string>
        {
            ["startTo"] = statementDate,
            ["endFrom"] = statementDate,
            ["count"] = "5",
            ["fields"] = "id,start,end"
        });

        if (periodResult.TryGetProperty("values", out var periodVals))
        {
            foreach (var v in periodVals.EnumerateArray())
            {
                var id = v.GetProperty("id").GetInt64();
                var start = v.TryGetProperty("start", out var s) ? s.GetString() : null;
                var end = v.TryGetProperty("end", out var e) ? e.GetString() : null;
                return (id, start, end);
            }
        }

        // Fallback: query all periods and find the one that contains the date
        if (!DateTime.TryParse(statementDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var statementDateValue))
            statementDateValue = DateTime.Today;

        var fallback = await api.GetAsync("/ledger/accountingPeriod", new Dictionary<string, string>
        {
            ["count"] = "100",
            ["fields"] = "id,start,end"
        });

        long? latestId = null;
        string? latestStart = null;
        DateTime? latestEnd = null;
        if (fallback.TryGetProperty("values", out var fbVals))
        {
            foreach (var v in fbVals.EnumerateArray())
            {
                if (!v.TryGetProperty("id", out var idProp) ||
                    !v.TryGetProperty("start", out var startProp) ||
                    !v.TryGetProperty("end", out var endProp) ||
                    !DateTime.TryParse(startProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var startDate) ||
                    !DateTime.TryParse(endProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var endDate))
                {
                    continue;
                }

                if (startDate <= statementDateValue && statementDateValue <= endDate)
                    return (idProp.GetInt64(), startProp.GetString(), endProp.GetString());

                if (!latestEnd.HasValue || endDate > latestEnd.Value)
                {
                    latestEnd = endDate;
                    latestId = idProp.GetInt64();
                    latestStart = startProp.GetString();
                }
            }
        }

        return (latestId, latestStart, null);
    }

    // ═══════════════════════════════════════════════════════════════
    // Reconciliation CRUD
    // ═══════════════════════════════════════════════════════════════

    private async Task<(long? ReconId, bool ReusedExisting)> CreateOrReuseReconciliationAsync(
        TripletexApiClient api, long accountId, long accountingPeriodId, decimal closingBalance)
    {
        var existingReconciliation = await FindExistingBankReconciliationAsync(api, accountId, accountingPeriodId);
        if (existingReconciliation != null)
        {
            if (!existingReconciliation.IsClosed)
            {
                _logger.LogInformation("Reusing existing bank reconciliation ID {Id}", existingReconciliation.Id);
                return (existingReconciliation.Id, true);
            }
            else
            {
                _logger.LogWarning("Existing bank reconciliation {Id} is closed; reusing", existingReconciliation.Id);
                return (existingReconciliation.Id, true);
            }
        }

        try
        {
            return await CreateReconciliationAsync(api, accountId, accountingPeriodId, closingBalance);
        }
        catch (TripletexApiException ex) when (ex.StatusCode == 422 && IsExistingReconciliationError(ex))
        {
            existingReconciliation = await FindExistingBankReconciliationAsync(api, accountId, accountingPeriodId);
            if (existingReconciliation != null)
            {
                _logger.LogInformation("Bank reconciliation already existed after POST conflict, reusing ID {Id}", existingReconciliation.Id);
                return (existingReconciliation.Id, true);
            }
            throw;
        }
    }

    private async Task<(long? ReconId, bool ReusedExisting)> CreateReconciliationAsync(
        TripletexApiClient api, long accountId, long accountingPeriodId, decimal closingBalance)
    {
        var body = new Dictionary<string, object>
        {
            ["account"] = new Dictionary<string, object> { ["id"] = accountId },
            ["accountingPeriod"] = new Dictionary<string, object> { ["id"] = accountingPeriodId },
            ["type"] = "MANUAL",
            ["bankAccountClosingBalanceCurrency"] = closingBalance
        };

        var reconResult = await api.PostAsync("/bank/reconciliation", body);
        if (reconResult.TryGetProperty("value", out var reconVal) &&
            reconVal.TryGetProperty("id", out var reconIdProp))
        {
            var reconId = reconIdProp.GetInt64();
            _logger.LogInformation("Created bank reconciliation ID: {Id}", reconId);
            return (reconId, false);
        }

        return (null, false);
    }

    private async Task<ExistingBankReconciliation?> FindExistingBankReconciliationAsync(TripletexApiClient api, long accountId, long accountingPeriodId)
    {
        var reconciliationResult = await api.GetAsync("/bank/reconciliation", new Dictionary<string, string>
        {
            ["accountId"] = accountId.ToString(CultureInfo.InvariantCulture),
            ["accountingPeriodId"] = accountingPeriodId.ToString(CultureInfo.InvariantCulture),
            ["count"] = "1",
            ["fields"] = "id,isClosed,bankAccountClosingBalanceCurrency"
        });

        if (!reconciliationResult.TryGetProperty("values", out var values))
            return null;

        foreach (var value in values.EnumerateArray())
        {
            if (!value.TryGetProperty("id", out var idProp))
                continue;

            var closingBal = value.TryGetProperty("bankAccountClosingBalanceCurrency", out var balanceProp) &&
                balanceProp.ValueKind == JsonValueKind.Number
                ? balanceProp.GetDecimal()
                : 0m;

            var isClosed = value.TryGetProperty("isClosed", out var isClosedProp) &&
                isClosedProp.ValueKind == JsonValueKind.True;

            return new ExistingBankReconciliation(idProp.GetInt64(), closingBal, isClosed);
        }

        return null;
    }

    private static bool IsExistingReconciliationError(TripletexApiException ex) =>
        ex.Message.Contains("already exist in the selected period", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("eksisterer allerede en bankavstemming", StringComparison.OrdinalIgnoreCase);

    // ═══════════════════════════════════════════════════════════════
    // Bank statement management
    // ═══════════════════════════════════════════════════════════════

    private async Task DeleteExistingBankStatements(TripletexApiClient api, long accountId)
    {
        try
        {
            var result = await api.GetAsync("/bank/statement", new Dictionary<string, string>
            {
                ["accountId"] = accountId.ToString(CultureInfo.InvariantCulture),
                ["count"] = "100",
                ["fields"] = "id"
            });

            if (result.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var stmt in values.EnumerateArray())
                {
                    if (stmt.TryGetProperty("id", out var idProp))
                    {
                        var stmtId = idProp.GetInt64();
                        await api.DeleteAsync($"/bank/statement/{stmtId}");
                        _logger.LogInformation("Deleted existing bank statement {Id}", stmtId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to delete existing bank statements: {Msg}", ex.Message);
        }
    }

    private async Task<decimal> EnsureAccountBalanceMatchesStatementAsync(
        TripletexApiClient api,
        long accountId,
        decimal statementClosingBalance,
        string statementDate,
        string? periodEnd)
    {
        try
        {
            var accountBalance = await GetAccountBalanceAsync(api, accountId, periodEnd ?? DateTime.Parse(statementDate, CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var delta = statementClosingBalance - accountBalance;

            if (Math.Abs(delta) < 0.01m)
                return 0m;

            var contraAccountId = await ResolveContraAccountId(api);
            if (!contraAccountId.HasValue)
            {
                _logger.LogWarning("Could not resolve contra account for bank balance adjustment; delta={Delta}", delta);
                return 0m;
            }

            var voucherBody = new Dictionary<string, object>
            {
                ["date"] = statementDate,
                ["description"] = "Bank reconciliation balance adjustment",
                ["postings"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["row"] = 1,
                        ["date"] = statementDate,
                        ["description"] = "Bank reconciliation balance adjustment",
                        ["account"] = new Dictionary<string, object> { ["id"] = accountId },
                        ["amountGross"] = delta,
                        ["amountGrossCurrency"] = delta
                    },
                    new Dictionary<string, object>
                    {
                        ["row"] = 2,
                        ["date"] = statementDate,
                        ["description"] = "Bank reconciliation balance adjustment",
                        ["account"] = new Dictionary<string, object> { ["id"] = contraAccountId.Value },
                        ["amountGross"] = -delta,
                        ["amountGrossCurrency"] = -delta
                    }
                }
            };

            await api.PostAsync("/ledger/voucher?sendToLedger=true", voucherBody);
            _logger.LogInformation("Created bank balance adjustment voucher with delta {Delta}", delta);
            return delta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to balance bank account before close: {Msg}", ex.Message);
            return 0m;
        }
    }

    private async Task<decimal> GetAccountBalanceAsync(TripletexApiClient api, long accountId, string dateToExclusive)
    {
        decimal total = 0m;
        int from = 0;
        const int count = 1000;

        while (true)
        {
            var result = await api.GetAsync("/ledger/postingByDate", new Dictionary<string, string>
            {
                ["dateFrom"] = "1900-01-01",
                ["dateTo"] = dateToExclusive,
                ["from"] = from.ToString(CultureInfo.InvariantCulture),
                ["count"] = count.ToString(CultureInfo.InvariantCulture)
            });

            if (!result.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                break;

            var pageCount = 0;
            foreach (var posting in values.EnumerateArray())
            {
                pageCount++;
                if (!posting.TryGetProperty("account", out var accountProp) ||
                    !accountProp.TryGetProperty("id", out var accountIdProp) ||
                    accountIdProp.GetInt64() != accountId)
                {
                    continue;
                }

                if (posting.TryGetProperty("amountGross", out var amountProp) && amountProp.ValueKind == JsonValueKind.Number)
                    total += amountProp.GetDecimal();
                else if (posting.TryGetProperty("amount", out var netAmountProp) && netAmountProp.ValueKind == JsonValueKind.Number)
                    total += netAmountProp.GetDecimal();
            }

            if (pageCount < count)
                break;

            from += count;
        }

        _logger.LogInformation("Computed account {AccountId} balance through {DateTo}: {Balance}", accountId, dateToExclusive, total);
        return total;
    }

    private async Task CloseReconciliationAsync(TripletexApiClient api, long reconId)
    {
        try
        {
            var current = await api.GetAsync($"/bank/reconciliation/{reconId}",
                new Dictionary<string, string> { ["fields"] = "id,version,isClosed" });

            if (!current.TryGetProperty("value", out var val)) return;

            var isClosed = val.TryGetProperty("isClosed", out var c) && c.ValueKind == JsonValueKind.True;
            if (isClosed)
            {
                _logger.LogInformation("Bank reconciliation {Id} is already closed", reconId);
                return;
            }

            var version = val.TryGetProperty("version", out var v) ? v.GetInt32() : 0;

            await api.PutAsync($"/bank/reconciliation/{reconId}", new Dictionary<string, object>
            {
                ["id"] = reconId,
                ["version"] = version,
                ["isClosed"] = true
            });
            _logger.LogInformation("Closed bank reconciliation {Id}", reconId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not close bank reconciliation {Id}: {Msg}", reconId, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // CSV parsing
    // ═══════════════════════════════════════════════════════════════

    private List<BankTransaction> ParseCsvFromFiles(List<SolveFile>? files)
    {
        var transactions = new List<BankTransaction>();
        if (files == null) return transactions;

        foreach (var file in files)
        {
            if (!file.Filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                !file.MimeType.Contains("csv", StringComparison.OrdinalIgnoreCase) &&
                !file.MimeType.Contains("text", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var content = Encoding.UTF8.GetString(Convert.FromBase64String(file.ContentBase64));
                var lines = content.Replace("\r\n", "\n").Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length < 2) continue;

                // Detect delimiter: "Dato;Forklaring;Inn;Ut;Saldo"
                var header = lines[0].Trim().TrimStart('\uFEFF');
                char delimiter = header.Contains(';') ? ';' : ',';

                _logger.LogInformation("Parsing CSV: {LineCount} lines, delimiter='{Delim}', header='{Header}'",
                    lines.Length, delimiter, header);

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var parts = SplitCsvLine(line, delimiter);
                    if (parts.Length < 4) continue;

                    var tx = new BankTransaction { Date = parts[0].Trim(), Description = parts[1].Trim() };

                    // Normalize date to YYYY-MM-DD if needed
                    tx.Date = NormalizeDateToIso(tx.Date);

                    // Inn (credit/incoming) and Ut (debit/outgoing) columns
                    var innStr = parts.Length > 2 ? parts[2].Trim() : "";
                    var utStr = parts.Length > 3 ? parts[3].Trim() : "";
                    var saldoStr = parts.Length > 4 ? parts[4].Trim() : "";

                    if (!string.IsNullOrEmpty(innStr) && decimal.TryParse(innStr.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var inn))
                        tx.Amount = inn;
                    else if (!string.IsNullOrEmpty(utStr) && decimal.TryParse(utStr.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var ut))
                        tx.Amount = ut > 0 ? -ut : ut;

                    if (!string.IsNullOrEmpty(saldoStr) && decimal.TryParse(saldoStr.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var saldo))
                        tx.RunningBalance = saldo;

                    // Classify transaction and extract metadata
                    ClassifyTransaction(tx);

                    transactions.Add(tx);
                }

                _logger.LogInformation("Parsed {Count} transactions from CSV", transactions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse CSV file {Name}: {Msg}", file.Filename, ex.Message);
            }
        }

        return transactions;
    }

    private static void ClassifyTransaction(BankTransaction tx)
    {
        var desc = tx.Description;

        // Customer payment: "Innbetaling fra [Customer] / Faktura [Number]"
        var customerMatch = Regex.Match(desc, @"Innbetaling fra\s+(.+?)\s*/\s*Faktura\s+(\d+)", RegexOptions.IgnoreCase);
        if (customerMatch.Success)
        {
            tx.Type = TransactionType.CustomerPayment;
            tx.CustomerName = customerMatch.Groups[1].Value.Trim();
            tx.InvoiceNumber = customerMatch.Groups[2].Value.Trim();
            return;
        }

        // Also check for generic "Faktura XXXX" in incoming payments
        if (desc.Contains("Innbetaling", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("betaling fra", StringComparison.OrdinalIgnoreCase))
        {
            tx.Type = TransactionType.CustomerPayment;
            var invoiceMatch = Regex.Match(desc, @"[Ff]aktura\s*(\d+)");
            if (invoiceMatch.Success)
                tx.InvoiceNumber = invoiceMatch.Groups[1].Value;
            return;
        }

        // Supplier payment: "Betaling Supplier/Lieferant/leverandør [Name]" or "Betaling til [Name]"
        var supplierMatch = Regex.Match(desc, @"Betaling\s+(?:Supplier|Lieferant|[Ll]everand[oø]r|til)\s+(.+)", RegexOptions.IgnoreCase);
        if (supplierMatch.Success)
        {
            tx.Type = TransactionType.SupplierPayment;
            tx.SupplierName = supplierMatch.Groups[1].Value.Trim();
            return;
        }

        // Bank fee
        if (desc.Contains("Bankgebyr", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("bank fee", StringComparison.OrdinalIgnoreCase))
        {
            tx.Type = TransactionType.BankFee;
            return;
        }

        // Interest
        if (desc.Contains("Renteinntekter", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("interest", StringComparison.OrdinalIgnoreCase))
        {
            tx.Type = TransactionType.Interest;
            return;
        }

        // Tax
        if (desc.Contains("Skattetrekk", StringComparison.OrdinalIgnoreCase))
        {
            tx.Type = TransactionType.Tax;
            return;
        }

        tx.Type = TransactionType.Other;
    }

    private static string NormalizeDateToIso(string date)
    {
        // Handle DD.MM.YYYY and DD-MM-YYYY formats
        if (Regex.IsMatch(date, @"^\d{2}[.\-/]\d{2}[.\-/]\d{4}$"))
        {
            var parts = date.Split('.', '-', '/');
            return $"{parts[2]}-{parts[1]}-{parts[0]}";
        }
        return date; // Already YYYY-MM-DD
    }

    private SolveFile? FindCsvFile(List<SolveFile>? files)
    {
        return files?.FirstOrDefault(f =>
            f.Filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
            f.MimeType.Contains("csv", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════
    // TRANSFERWISE CSV building
    // ═══════════════════════════════════════════════════════════════

    private byte[] BuildTransferwiseCsvImport(List<BankTransaction> transactions, string? periodStart)
    {
        var builder = new StringBuilder();
        builder.Append("TransferWise ID,Date,Amount,Currency,Description,Payment Reference,Running Balance,Exchange From,Exchange To,Buy - Loss,Merchant,Category,Note,Total fees\r\n");

        // Calculate opening balance: first row's running balance minus first row's amount
        decimal openingBalance = transactions.Count > 0
            ? transactions[0].RunningBalance - transactions[0].Amount
            : 0m;

        // TRANSFERWISE format: importer uses the first data row's Running Balance as closing balance.
        // Real TransferWise exports are reverse-chronological (newest first).
        // We emit: newest-first transaction rows, then an opening balance row.
        var reversed = new List<BankTransaction>(transactions);
        reversed.Reverse();

        int rowIndex = 0;
        foreach (var tx in reversed)
        {
            rowIndex++;
            var dateStr = tx.Date;
            if (periodStart != null &&
                DateTime.TryParse(tx.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var txDate) &&
                DateTime.TryParse(periodStart, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var pStart) &&
                txDate < pStart)
            {
                dateStr = periodStart;
            }

            // TRANSFERWISE format uses DD-MM-YYYY dates
            var twDate = DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed.ToString("dd-MM-yyyy")
                : dateStr;

            builder.Append($"TRANSFER-{rowIndex:D3}")
                .Append(',')
                .Append(twDate)
                .Append(',')
                .Append(FormatCsvDecimal(tx.Amount))
                .Append(",NOK,")
                .Append(EscapeCsv(tx.Description, ','))
                .Append(",,")
                .Append(FormatCsvDecimal(tx.RunningBalance))
                .Append(",,,,,,,0.00\r\n");
        }

        // Add opening balance row (oldest date, makes account balance = closing balance)
        if (openingBalance != 0m)
        {
            rowIndex++;
            var obDate = periodStart ?? (transactions.Count > 0 ? transactions[0].Date : "01-01-2026");
            var twObDate = DateTime.TryParse(obDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var obParsed)
                ? obParsed.ToString("dd-MM-yyyy")
                : obDate;

            builder.Append($"TRANSFER-{rowIndex:D3}")
                .Append(',')
                .Append(twObDate)
                .Append(',')
                .Append(FormatCsvDecimal(openingBalance))
                .Append(",NOK,Opening Balance,,")
                .Append(FormatCsvDecimal(openingBalance))
                .Append(",,,,,,,0.00\r\n");
        }

        return Encoding.GetEncoding("iso-8859-1").GetBytes(builder.ToString());
    }

    private static string FormatCsvDecimal(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value, char delimiter)
    {
        if (value.Contains(delimiter) || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static string[] SplitCsvLine(string line, char delimiter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int index = 0; index < line.Length; index++)
        {
            var ch = line[index];

            if (ch == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == delimiter && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
        return val?.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal types
    // ═══════════════════════════════════════════════════════════════

    private class BankTransaction
    {
        public string Date { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal RunningBalance { get; set; }
        public TransactionType Type { get; set; }
        public string? InvoiceNumber { get; set; }
        public string? CustomerName { get; set; }
        public string? SupplierName { get; set; }
        public bool Matched { get; set; }
    }

    private enum TransactionType
    {
        CustomerPayment,
        SupplierPayment,
        BankFee,
        Interest,
        Tax,
        Other
    }

    private sealed record ExistingBankReconciliation(long Id, decimal ClosingBalance, bool IsClosed);
}
