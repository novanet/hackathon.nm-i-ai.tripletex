using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles bank_reconciliation tasks.
/// Full flow: resolve account → resolve period → create reconciliation → parse CSV → try import/suggest-match → fallback to adjustments
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
        long? accountingPeriodId = null;
        var periodResult = await api.GetAsync("/ledger/accountingPeriod", new Dictionary<string, string>
        {
            ["startTo"] = date,
            ["endFrom"] = date,
            ["count"] = "5",
            ["fields"] = "id,start,end"
        });
        if (periodResult.TryGetProperty("values", out var periodVals))
        {
            foreach (var v in periodVals.EnumerateArray())
            {
                accountingPeriodId = v.GetProperty("id").GetInt64();
                break;
            }
        }

        if (!accountingPeriodId.HasValue)
        {
            // Fallback: try getting all periods and pick the last one
            var fallback = await api.GetAsync("/ledger/accountingPeriod", new Dictionary<string, string>
            {
                ["count"] = "100",
                ["fields"] = "id,start,end"
            });
            if (fallback.TryGetProperty("values", out var fbVals))
            {
                JsonElement? latest = null;
                foreach (var v in fbVals.EnumerateArray())
                {
                    latest = v; // just pick the last one
                }
                if (latest.HasValue)
                    accountingPeriodId = latest.Value.GetProperty("id").GetInt64();
            }
        }

        if (!accountingPeriodId.HasValue)
        {
            _logger.LogWarning("Could not resolve accounting period for date {Date} — aborting", date);
            return HandlerResult.Empty;
        }

        // Step 3: Create manual bank reconciliation
        var body = new Dictionary<string, object>
        {
            ["account"] = new { id = accountId.Value },
            ["accountingPeriod"] = new { id = accountingPeriodId.Value },
            ["type"] = "MANUAL",
            ["bankAccountClosingBalanceCurrency"] = closingBalance.Value
        };

        var reconResult = await api.PostAsync("/bank/reconciliation", body);

        long? reconId = null;
        if (reconResult.TryGetProperty("value", out var reconVal))
            reconId = reconVal.GetProperty("id").GetInt64();

        _logger.LogInformation("Created bank reconciliation ID: {Id}", reconId);

        // Step 4: If we have CSV transactions, try bank statement import then auto-match
        if (reconId.HasValue && transactions.Count > 0)
        {
            await TryImportAndMatch(api, reconId.Value, accountId.Value, transactions, date, extracted.Files);
        }

        return new HandlerResult { EntityType = "reconciliation", EntityId = reconId };
    }

    private async Task TryImportAndMatch(TripletexApiClient api, long reconId, long accountId, List<BankTransaction> transactions, string endDate, List<SolveFile>? files)
    {
        // Try bank statement import with various formats
        var csvFile = FindCsvFile(files);
        if (csvFile != null)
        {
            var csvBytes = Convert.FromBase64String(csvFile.ContentBase64);
            var formats = new[] { "DNB_CSV", "NORDEA_CSV", "DANSKE_BANK_CSV", "SBANKEN_PRIVAT_CSV" };

            var fromDate = transactions[0].Date;
            var toDate = endDate;

            foreach (var format in formats)
            {
                try
                {
                    _logger.LogInformation("Trying bank statement import with format {Format}", format);
                    var importResult = await api.PostBankStatementImportAsync(
                        accountId, fromDate, toDate, format, csvBytes, csvFile.Filename);
                    _logger.LogInformation("Bank statement import succeeded with format {Format}", format);

                    // Try auto-suggest matches
                    try
                    {
                        await api.PutAsync($"/bank/reconciliation/match/:suggest?bankReconciliationId={reconId}", new { });
                        _logger.LogInformation("Auto-match suggest completed for reconciliation {Id}", reconId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Auto-match suggest failed: {Msg}", ex.Message);
                    }

                    return; // Import succeeded, done
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Import with format {Format} failed: {Msg}", format, ex.Message);
                }
            }
        }

        // Fallback: create adjustments for each transaction
        _logger.LogInformation("Import path unavailable — creating adjustments for {Count} transactions", transactions.Count);
        await CreateAdjustments(api, reconId, transactions);
    }

    private async Task CreateAdjustments(TripletexApiClient api, long reconId, List<BankTransaction> transactions)
    {
        // Resolve payment type for bank transfers
        long? paymentTypeId = null;
        try
        {
            var ptResult = await api.GetAsync("/ledger/paymentTypeOut", new Dictionary<string, string>
            {
                ["count"] = "5",
                ["fields"] = "id,description"
            });
            if (ptResult.TryGetProperty("values", out var ptVals))
            {
                foreach (var v in ptVals.EnumerateArray())
                {
                    paymentTypeId = v.GetProperty("id").GetInt64();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not resolve payment type: {Msg}", ex.Message);
        }

        var adjustments = new List<Dictionary<string, object>>();
        foreach (var tx in transactions)
        {
            var adj = new Dictionary<string, object>
            {
                ["date"] = tx.Date,
                ["description"] = tx.Description,
                ["amount"] = tx.Amount
            };
            if (paymentTypeId.HasValue)
                adj["paymentType"] = new { id = paymentTypeId.Value };
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
                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length < 2) continue;

                // Detect delimiter and header pattern: "Dato;Forklaring;Inn;Ut;Saldo"
                var header = lines[0].Trim();
                char delimiter = header.Contains(';') ? ';' : ',';

                _logger.LogInformation("Parsing CSV: {LineCount} lines, delimiter='{Delim}', header='{Header}'",
                    lines.Length, delimiter, header);

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var parts = line.Split(delimiter);
                    if (parts.Length < 4) continue;

                    var tx = new BankTransaction { Date = parts[0].Trim(), Description = parts[1].Trim() };

                    // Inn (credit/incoming) and Ut (debit/outgoing) columns
                    var innStr = parts.Length > 2 ? parts[2].Trim() : "";
                    var utStr = parts.Length > 3 ? parts[3].Trim() : "";
                    var saldoStr = parts.Length > 4 ? parts[4].Trim() : "";

                    if (!string.IsNullOrEmpty(innStr) && decimal.TryParse(innStr.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var inn))
                        tx.Amount = inn;
                    else if (!string.IsNullOrEmpty(utStr) && decimal.TryParse(utStr.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var ut))
                        tx.Amount = ut; // Already negative in the CSV format

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
}
