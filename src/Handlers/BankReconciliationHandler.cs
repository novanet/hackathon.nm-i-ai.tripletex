using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles bank_reconciliation tasks.
/// Full flow:
///   1. Parse CSV → classify transactions (customer payment, supplier payment, other)
///   2. Register payments on open customer invoices (by invoice number from CSV)
///   3. Register payments on open supplier invoices (by supplier name from CSV)
///   4. Create bank reconciliation → import bank statement → auto-match → close
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
        // Fallback: try raw_amounts — use the LAST amount which is typically the closing balance
        if (!closingBalance.HasValue && extracted.RawAmounts.Count > 0)
        {
            var lastAmountStr = extracted.RawAmounts[^1].Replace(",", ".");
            if (decimal.TryParse(lastAmountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rawParsed))
                closingBalance = rawParsed;
        }

        // Extract date (statement date / period end)
        var date = GetStringField(rec, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[^1] : null)
            ?? DateTime.Today.ToString("yyyy-MM-dd");

        // Parse CSV from attached files
        var transactions = ParseCsvFromFiles(extracted.Files);

        // If we got transactions and no closing balance yet, use the last transaction's running balance
        if (!closingBalance.HasValue && transactions.Count > 0)
            closingBalance = transactions[^1].RunningBalance;
        closingBalance ??= 0m;

        // If we got transactions, derive the date from the last transaction
        if (transactions.Count > 0)
            date = transactions[^1].Date;

        _logger.LogInformation("BankReconciliation: account={Account}, balance={Balance}, date={Date}, transactions={TxCount}",
            accountNumber, closingBalance, date, transactions.Count);

        // ── PHASE 1: Register payments on open invoices ──
        int customerPaymentsRegistered = 0;
        int supplierPaymentsRegistered = 0;

        if (transactions.Count > 0)
        {
            // Resolve payment type once (needed for customer invoice payments)
            var paymentTypeId = await ResolveInvoicePaymentTypeId(api);

            // Process customer payments (incoming, with invoice numbers)
            var customerTxs = transactions.Where(t => t.Type == TransactionType.CustomerPayment && !string.IsNullOrEmpty(t.InvoiceNumber)).ToList();
            foreach (var tx in customerTxs)
            {
                try
                {
                    var invoiceId = await FindCustomerInvoiceByNumber(api, tx.InvoiceNumber!);
                    if (invoiceId.HasValue)
                    {
                        await RegisterCustomerInvoicePayment(api, invoiceId.Value, paymentTypeId, Math.Abs(tx.Amount), tx.Date);
                        tx.Matched = true;
                        customerPaymentsRegistered++;
                        _logger.LogInformation("Registered customer payment: Faktura {InvoiceNum} amount={Amount} on invoice {InvoiceId}",
                            tx.InvoiceNumber, tx.Amount, invoiceId.Value);
                    }
                    else
                    {
                        _logger.LogWarning("Could not find customer invoice with number {InvoiceNum}", tx.InvoiceNumber);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to register customer payment for Faktura {InvoiceNum}: {Msg}", tx.InvoiceNumber, ex.Message);
                }
            }

            // Process supplier payments (outgoing)
            var supplierTxs = transactions.Where(t => t.Type == TransactionType.SupplierPayment && !string.IsNullOrEmpty(t.SupplierName)).ToList();
            foreach (var tx in supplierTxs)
            {
                try
                {
                    var paid = await FindAndPaySupplierInvoice(api, tx.SupplierName!, Math.Abs(tx.Amount), tx.Date);
                    if (paid)
                    {
                        tx.Matched = true;
                        supplierPaymentsRegistered++;
                        _logger.LogInformation("Registered supplier payment: {Supplier} amount={Amount}", tx.SupplierName, Math.Abs(tx.Amount));
                    }
                    else
                    {
                        _logger.LogWarning("Could not find/pay supplier invoice for {Supplier} amount={Amount}", tx.SupplierName, Math.Abs(tx.Amount));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to register supplier payment for {Supplier}: {Msg}", tx.SupplierName, ex.Message);
                }
            }

            _logger.LogInformation("Payment registration: {CustPaid} customer, {SupPaid} supplier payments registered",
                customerPaymentsRegistered, supplierPaymentsRegistered);
        }

        // ── PHASE 2: Create bank reconciliation ──

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

        // Step 2: Resolve accounting period containing the statement date
        var (accountingPeriodId, periodStart, periodEnd) = await ResolveAccountingPeriodAsync(api, date);

        if (!accountingPeriodId.HasValue)
        {
            _logger.LogWarning("Could not resolve accounting period for date {Date} — aborting", date);
            return HandlerResult.Empty;
        }

        // Step 3: Create or reuse bank reconciliation
        var (reconId, reusedExistingReconciliation) = await CreateOrReuseReconciliationAsync(
            api,
            accountId.Value,
            accountingPeriodId.Value,
            closingBalance.Value);

        var result = new HandlerResult { EntityType = "reconciliation", EntityId = reconId };

        if (reconId.HasValue)
        {
            result.Metadata["accountNumber"] = accountNumber;
            result.Metadata["statementDate"] = date;
            result.Metadata["closingBalance"] = closingBalance.Value.ToString("0.00", CultureInfo.InvariantCulture);
            result.Metadata["transactionCount"] = transactions.Count.ToString(CultureInfo.InvariantCulture);
            result.Metadata["customerPaymentsRegistered"] = customerPaymentsRegistered.ToString(CultureInfo.InvariantCulture);
            result.Metadata["supplierPaymentsRegistered"] = supplierPaymentsRegistered.ToString(CultureInfo.InvariantCulture);
        }

        // ── PHASE 3: Import bank statement and match ──
        if (reconId.HasValue && transactions.Count > 0)
        {
            var csvFile = FindCsvFile(extracted.Files);
            if (csvFile != null)
            {
                var importOutcome = await ImportAndMatchBankStatement(api, reconId.Value, accountId.Value, transactions, date, periodStart, periodEnd);
                result.Metadata["bankImportMode"] = importOutcome.Mode;
                result.Metadata["bankImportSucceeded"] = importOutcome.ImportSucceeded ? "true" : "false";
                result.Metadata["matchCount"] = importOutcome.MatchCount.ToString(CultureInfo.InvariantCulture);
            }
        }

        // ── PHASE 4: Close reconciliation ──
        if (reconId.HasValue)
        {
            await CloseReconciliationAsync(api, reconId.Value);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Payment registration helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<long?> FindCustomerInvoiceByNumber(TripletexApiClient api, string invoiceNumber)
    {
        var result = await api.GetAsync("/invoice", new Dictionary<string, string>
        {
            ["invoiceNumber"] = invoiceNumber,
            ["count"] = "1",
            ["fields"] = "id,invoiceNumber,amount,amountOutstanding"
        });

        if (result.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vals.EnumerateArray())
            {
                if (v.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetInt64();
                    _logger.LogInformation("Found customer invoice #{Num}: ID={Id}", invoiceNumber, id);
                    return id;
                }
            }
        }

        return null;
    }

    private async Task RegisterCustomerInvoicePayment(TripletexApiClient api, long invoiceId, long paymentTypeId, decimal amount, string paymentDate)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["paymentDate"] = paymentDate,
            ["paymentTypeId"] = paymentTypeId.ToString(CultureInfo.InvariantCulture),
            ["paidAmount"] = amount.ToString("F2", CultureInfo.InvariantCulture)
        };

        await api.PutAsync(
            $"/invoice/{invoiceId}/:payment",
            body: null,
            queryParams: queryParams);
    }

    private async Task<bool> FindAndPaySupplierInvoice(TripletexApiClient api, string supplierName, decimal amount, string paymentDate)
    {
        // Step 1: Find the supplier by name
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

        if (supplierIds.Count == 0)
        {
            _logger.LogWarning("No supplier found with name '{Name}'", supplierName);
            return false;
        }

        // Step 2: Find open supplier invoices for this supplier
        foreach (var supplierId in supplierIds)
        {
            var invoiceResult = await api.GetAsync("/supplierInvoice", new Dictionary<string, string>
            {
                ["supplierId"] = supplierId.ToString(CultureInfo.InvariantCulture),
                ["from"] = "0",
                ["count"] = "100",
                ["fields"] = "id,amount,amountCurrency,invoiceNumber,supplierId"
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
    // Bank reconciliation + statement import + matching
    // ═══════════════════════════════════════════════════════════════

    private async Task<ImportOutcome> ImportAndMatchBankStatement(
        TripletexApiClient api, long reconId, long accountId,
        List<BankTransaction> transactions, string endDate, string? periodStart, string? periodEnd)
    {
        var transformedBytes = BuildTransferwiseCsvImport(transactions, periodStart);
        var transformedFileName = "bankstatement-transferwise.csv";
        var fromDate = periodStart ?? transactions[0].Date;
        var toDate = periodEnd ?? endDate;

        // Delete any pre-existing bank statement for this account to avoid duplicates
        await DeleteExistingBankStatements(api, accountId);

        JsonElement importResult;
        try
        {
            importResult = await api.PostBankStatementImportAsync(accountId, fromDate, toDate, "TRANSFERWISE", transformedBytes, transformedFileName);
            _logger.LogInformation("Bank statement import succeeded for reconciliation {Id}", reconId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Bank statement import failed for reconciliation {Id}: {Msg}", reconId, ex.Message);
            return new ImportOutcome("import_failed", false, 0);
        }

        // Try auto-suggest — Tripletex matches bank transactions to existing payment postings
        int matchCount = 0;
        try
        {
            var suggestResult = await api.PutAsync("/bank/reconciliation/match/:suggest", null,
                new Dictionary<string, string> { ["bankReconciliationId"] = reconId.ToString(CultureInfo.InvariantCulture) });
            if (suggestResult.TryGetProperty("values", out var suggestValues) && suggestValues.ValueKind == JsonValueKind.Array)
            {
                matchCount = suggestValues.GetArrayLength();
            }
            _logger.LogInformation("Suggest matching: {Count} matches auto-created for reconciliation {Id}", matchCount, reconId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Suggest matching failed for reconciliation {Id}: {Msg}", reconId, ex.Message);
        }

        return new ImportOutcome("transferwise_import_with_matching", true, matchCount);
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

        int rowIndex = 0;
        foreach (var tx in transactions)
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
    private sealed record ImportOutcome(string Mode, bool ImportSucceeded, int MatchCount);
}
