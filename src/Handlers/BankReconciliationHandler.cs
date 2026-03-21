using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles bank_reconciliation tasks.
/// Full flow: resolve account → resolve period → create reconciliation → transform/import CSV → suggest matches → fallback to adjustments
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

        // Extract date (statement date / period end)
        var date = GetStringField(rec, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : null)
            ?? DateTime.Today.ToString("yyyy-MM-dd");

        // Parse CSV from attached files
        var transactions = ParseCsvFromFiles(extracted.Files);

        // If we got transactions and no closing balance yet, use the last transaction's running balance
        if (!closingBalance.HasValue && transactions.Count > 0)
            closingBalance = transactions[^1].RunningBalance;
        closingBalance ??= 0m;

        // If we got transactions, also derive the date range
        if (transactions.Count > 0 && date == DateTime.Today.ToString("yyyy-MM-dd"))
            date = transactions[^1].Date;

        _logger.LogInformation("BankReconciliation: account={Account}, balance={Balance}, date={Date}, transactions={TxCount}",
            accountNumber, closingBalance, date, transactions.Count);

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

        // Step 3: Reuse an existing reconciliation in the same period when replaying in sandbox.
        var (reconId, reusedExistingReconciliation) = await CreateOrReuseReconciliationAsync(
            api,
            accountId.Value,
            accountingPeriodId.Value,
            closingBalance.Value);

        // Step 4: If we have CSV transactions, try bank statement import then auto-match
        var result = new HandlerResult { EntityType = "reconciliation", EntityId = reconId };

        if (reconId.HasValue)
        {
            result.Metadata["accountNumber"] = accountNumber;
            result.Metadata["statementDate"] = date;
            result.Metadata["closingBalance"] = closingBalance.Value.ToString("0.00", CultureInfo.InvariantCulture);
            result.Metadata["transactionCount"] = transactions.Count.ToString(CultureInfo.InvariantCulture);
            result.Metadata["reusedExistingReconciliation"] = reusedExistingReconciliation ? "true" : "false";
        }

        if (reconId.HasValue && transactions.Count > 0)
        {
            var importOutcome = await TryImportAndMatch(api, reconId.Value, accountId.Value, transactions, date, periodStart, periodEnd, extracted.Files);
            result.Metadata["bankImportMode"] = importOutcome.Mode;
            result.Metadata["bankImportSucceeded"] = importOutcome.ImportSucceeded ? "true" : "false";
            result.Metadata["adjustmentCount"] = importOutcome.AdjustmentCount.ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

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

    private async Task<(long? ReconId, bool ReusedExisting)> CreateOrReuseReconciliationAsync(
        TripletexApiClient api,
        long accountId,
        long accountingPeriodId,
        decimal closingBalance)
    {
        var existingReconciliation = await FindExistingBankReconciliationAsync(api, accountId, accountingPeriodId);
        if (existingReconciliation != null)
        {
            if (CanReuseExistingReconciliation(existingReconciliation, closingBalance))
            {
                _logger.LogInformation(
                    "Reusing empty matching bank reconciliation ID {Id} for account {AccountId} and accounting period {AccountingPeriodId}",
                    existingReconciliation.Id,
                    accountId,
                    accountingPeriodId);
                return (existingReconciliation.Id, true);
            }

            if (!existingReconciliation.IsClosed)
            {
                _logger.LogInformation(
                    "Deleting stale bank reconciliation ID {Id} for account {AccountId} and accounting period {AccountingPeriodId}; balance={ExistingBalance}, targetBalance={TargetBalance}, transactions={TransactionCount}",
                    existingReconciliation.Id,
                    accountId,
                    accountingPeriodId,
                    existingReconciliation.ClosingBalance,
                    closingBalance,
                    existingReconciliation.TransactionCount);
                await api.DeleteAsync($"/bank/reconciliation/{existingReconciliation.Id}");
            }
            else
            {
                _logger.LogWarning(
                    "Existing bank reconciliation ID {Id} is closed and does not match the requested replay context; reusing because it cannot be safely reset",
                    existingReconciliation.Id);
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
                if (CanReuseExistingReconciliation(existingReconciliation, closingBalance))
                {
                    _logger.LogInformation(
                        "Bank reconciliation already existed after POST, reusing empty matching ID {Id} for account {AccountId} and accounting period {AccountingPeriodId}",
                        existingReconciliation.Id,
                        accountId,
                        accountingPeriodId);
                    return (existingReconciliation.Id, true);
                }

                if (!existingReconciliation.IsClosed)
                {
                    _logger.LogInformation(
                        "Bank reconciliation already existed after POST but does not match the requested replay context; deleting ID {Id} and recreating",
                        existingReconciliation.Id);
                    await api.DeleteAsync($"/bank/reconciliation/{existingReconciliation.Id}");
                    return await CreateReconciliationAsync(api, accountId, accountingPeriodId, closingBalance);
                }
            }

            throw;
        }
    }

    private async Task<(long? ReconId, bool ReusedExisting)> CreateReconciliationAsync(
        TripletexApiClient api,
        long accountId,
        long accountingPeriodId,
        decimal closingBalance)
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
            ["fields"] = "id,isClosed,bankAccountClosingBalanceCurrency,transactions(id)"
        });

        if (!reconciliationResult.TryGetProperty("values", out var values))
            return null;

        foreach (var value in values.EnumerateArray())
        {
            if (!value.TryGetProperty("id", out var idProp))
                continue;

            var closingBalance = value.TryGetProperty("bankAccountClosingBalanceCurrency", out var balanceProp) &&
                balanceProp.ValueKind == JsonValueKind.Number
                ? balanceProp.GetDecimal()
                : 0m;

            var isClosed = value.TryGetProperty("isClosed", out var isClosedProp) &&
                isClosedProp.ValueKind == JsonValueKind.True;

            var transactionCount = value.TryGetProperty("transactions", out var transactionsProp) &&
                transactionsProp.ValueKind == JsonValueKind.Array
                ? transactionsProp.GetArrayLength()
                : 0;

            return new ExistingBankReconciliation(idProp.GetInt64(), closingBalance, isClosed, transactionCount);
        }

        return null;
    }

    private static bool CanReuseExistingReconciliation(ExistingBankReconciliation reconciliation, decimal targetClosingBalance) =>
        !reconciliation.IsClosed &&
        reconciliation.TransactionCount == 0 &&
        DecimalEquals(reconciliation.ClosingBalance, targetClosingBalance);

    private static bool DecimalEquals(decimal left, decimal right) => Math.Abs(left - right) < 0.01m;

    private static bool IsExistingReconciliationError(TripletexApiException ex) =>
        ex.Message.Contains("already exist in the selected period", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("eksisterer allerede en bankavstemming", StringComparison.OrdinalIgnoreCase);

    private async Task<ImportOutcome> TryImportAndMatch(TripletexApiClient api, long reconId, long accountId, List<BankTransaction> transactions, string endDate, string? periodStart, string? periodEnd, List<SolveFile>? files)
    {
        var csvFile = FindCsvFile(files);
        if (csvFile != null)
        {
            var transformedBytes = BuildTransferwiseCsvImport(transactions, periodStart);
            var transformedFileName = Path.GetFileNameWithoutExtension(csvFile.Filename) + "-transferwise.csv";
            var fromDate = periodStart ?? transactions[0].Date;
            var toDate = periodEnd ?? endDate;

            // Delete any pre-existing bank statement for this account/period to avoid "already exists" errors
            await DeleteExistingBankStatements(api, accountId);

            try
            {
                _logger.LogInformation("Trying bank statement import with TRANSFERWISE format for reconciliation {Id}", reconId);
                await api.PostBankStatementImportAsync(accountId, fromDate, toDate, "TRANSFERWISE", transformedBytes, transformedFileName);
                _logger.LogInformation("Bank statement import succeeded with TRANSFERWISE for reconciliation {Id}", reconId);

                try
                {
                    await api.PutAsync("/bank/reconciliation/match/:suggest", new Dictionary<string, object>(),
                        new Dictionary<string, string> { ["bankReconciliationId"] = reconId.ToString(CultureInfo.InvariantCulture) });
                    _logger.LogInformation("Auto-match suggest completed for reconciliation {Id}", reconId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Auto-match suggest failed after import for reconciliation {Id}: {Msg}", reconId, ex.Message);
                }

                return new ImportOutcome("transferwise_import", true, 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("TRANSFERWISE import failed for reconciliation {Id}: {Msg}", reconId, ex.Message);
            }
        }

        _logger.LogInformation("Import path unavailable — creating adjustments for {Count} transactions", transactions.Count);
        var adjustmentCount = await CreateAdjustments(api, reconId, transactions, periodStart);
        return new ImportOutcome("adjustments", false, adjustmentCount);
    }

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

    private async Task<int> CreateAdjustments(TripletexApiClient api, long reconId, List<BankTransaction> transactions, string? periodStart)
    {
        // Fetch a valid payment type — required by the adjustment endpoint
        long? paymentTypeId = await ResolvePaymentTypeIdAsync(api);
        if (!paymentTypeId.HasValue)
        {
            _logger.LogWarning("No bank reconciliation payment types available — cannot create adjustments");
            return 0;
        }

        var adjustments = new List<Dictionary<string, object>>();
        foreach (var tx in transactions)
        {
            // Clamp date to period start if transaction date is before the reconciliation period
            var adjustmentDate = tx.Date;
            if (periodStart != null &&
                DateTime.TryParse(tx.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var txDate) &&
                DateTime.TryParse(periodStart, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var pStart) &&
                txDate < pStart)
            {
                adjustmentDate = periodStart;
            }

            var adj = new Dictionary<string, object>
            {
                ["paymentType"] = new Dictionary<string, object> { ["id"] = paymentTypeId.Value },
                ["date"] = adjustmentDate,
                ["postingDate"] = adjustmentDate,
                ["description"] = tx.Description,
                ["amount"] = Math.Abs(tx.Amount)
            };
            adjustments.Add(adj);
        }

        if (adjustments.Count > 0)
        {
            try
            {
                await api.PutAsync($"/bank/reconciliation/{reconId}/:adjustment", adjustments);
                _logger.LogInformation("Created {Count} adjustments for reconciliation {Id}", adjustments.Count, reconId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to create adjustments: {Msg}", ex.Message);
            }
        }

        return adjustments.Count;
    }

    private async Task<long?> ResolvePaymentTypeIdAsync(TripletexApiClient api)
    {
        try
        {
            var result = await api.GetAsync("/bank/reconciliation/paymentType", new Dictionary<string, string>
            {
                ["count"] = "100",
                ["fields"] = "id,description"
            });

            if (result.TryGetProperty("values", out var values))
            {
                foreach (var v in values.EnumerateArray())
                {
                    if (v.TryGetProperty("id", out var idProp))
                    {
                        _logger.LogInformation("Using bank reconciliation payment type ID {Id}", idProp.GetInt64());
                        return idProp.GetInt64();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch bank reconciliation payment types: {Msg}", ex.Message);
        }

        return null;
    }

    private byte[] BuildTransferwiseCsvImport(List<BankTransaction> transactions, string? periodStart)
    {
        var builder = new StringBuilder();
        builder.Append("TransferWise ID,Date,Amount,Currency,Description,Payment Reference,Running Balance,Exchange From,Exchange To,Buy - Loss,Merchant,Category,Note,Total fees\r\n");

        int rowIndex = 0;
        foreach (var tx in transactions)
        {
            rowIndex++;
            // Clamp dates before the accounting period to the period start
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

            builder.Append($"TRANSFER-{rowIndex:D3}")  // TransferWise ID
                .Append(',')
                .Append(twDate)
                .Append(',')
                .Append(FormatCsvDecimal(tx.Amount))
                .Append(",NOK,")
                .Append(EscapeCsv(tx.Description, ','))
                .Append(",,")  // Payment Reference (empty)
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

                // Detect delimiter and header pattern: "Dato;Forklaring;Inn;Ut;Saldo"
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

                    // Classify transaction
                    tx.Type = ClassifyTransaction(tx.Description);

                    // Extract invoice number if present
                    var invoiceMatch = Regex.Match(tx.Description, @"[Ff]aktura\s*(\d+)");
                    if (invoiceMatch.Success)
                        tx.InvoiceNumber = invoiceMatch.Groups[1].Value;

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

    private SolveFile? FindCsvFile(List<SolveFile>? files)
    {
        return files?.FirstOrDefault(f =>
            f.Filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
            f.MimeType.Contains("csv", StringComparison.OrdinalIgnoreCase));
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

    private static TransactionType ClassifyTransaction(string description)
    {
        var lower = description.ToLowerInvariant();
        if (lower.Contains("innbetaling") || lower.Contains("betaling fra"))
            return TransactionType.CustomerPayment;
        if (lower.Contains("betaling leverand") || lower.Contains("betaling til"))
            return TransactionType.SupplierPayment;
        if (lower.Contains("bankgebyr") || lower.Contains("bank fee"))
            return TransactionType.BankFee;
        if (lower.Contains("skattetrekk") || lower.Contains("skatt"))
            return TransactionType.Tax;
        if (lower.Contains("lønn") || lower.Contains("salary"))
            return TransactionType.Salary;
        return TransactionType.Other;
    }

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
        return val?.ToString();
    }

    private class BankTransaction
    {
        public string Date { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal RunningBalance { get; set; }
        public TransactionType Type { get; set; }
        public string? InvoiceNumber { get; set; }
    }

    private enum TransactionType
    {
        CustomerPayment,
        SupplierPayment,
        BankFee,
        Tax,
        Salary,
        Other
    }

    private sealed record ExistingBankReconciliation(long Id, decimal ClosingBalance, bool IsClosed, int TransactionCount);

    private sealed record ImportOutcome(string Mode, bool ImportSucceeded, int AdjustmentCount);
}
