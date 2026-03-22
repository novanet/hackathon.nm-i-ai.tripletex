using System.Globalization;
using System.Text;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class VoucherHandler : ITaskHandler
{
    private readonly ILogger<VoucherHandler> _logger;

    // Minimal valid PDF used for importDocument to get an empty (postingless) voucher
    private static readonly byte[] MinimalPdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
        "xref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n" +
        "trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n190\n%%EOF\n");

    public VoucherHandler(ILogger<VoucherHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var voucher = extracted.Entities.GetValueOrDefault("voucher") ?? new();

        // --- Supplier invoice detection ---
        var supplierName = GetStringField(voucher, "supplierName");
        var supplierOrgNumber = GetStringField(voucher, "supplierOrgNumber");
        if (supplierName != null || supplierOrgNumber != null)
            return await HandleSupplierInvoice(api, extracted, voucher);

        // --- Multi-voucher detection (voucher1, voucher2, ...) ---
        var numberedVouchers = extracted.Entities
            .Where(kv => kv.Key.StartsWith("voucher", StringComparison.OrdinalIgnoreCase)
                      && kv.Key.Length > 7
                      && char.IsDigit(kv.Key[7]))
            .OrderBy(kv => kv.Key)
            .ToList();
        if (numberedVouchers.Count > 0 && voucher.Count == 0)
            return await HandleMultiVoucher(api, extracted, numberedVouchers);

        // --- Step 1: Create custom dimension + values if present ---
        int? dimensionIndex = null;
        long? linkedDimensionValueId = null;

        var dimensionEntity = extracted.Entities.GetValueOrDefault("dimension");
        // Fallback: dimension nested inside voucher entity
        if (dimensionEntity == null && voucher.TryGetValue("dimension", out var dimVal) && dimVal is JsonElement dimJe
            && dimJe.ValueKind == JsonValueKind.Object)
        {
            dimensionEntity = new Dictionary<string, object>();
            foreach (var prop in dimJe.EnumerateObject())
                dimensionEntity[prop.Name] = prop.Value;
        }
        if (dimensionEntity != null)
        {
            var dimName = GetStringField(dimensionEntity, "name");
            if (dimName != null)
            {
                // Search for existing dimension first
                var searchResult = await api.GetAsync("/ledger/accountingDimensionName/search",
                    new Dictionary<string, string> { ["count"] = "10", ["fields"] = "id,dimensionName,dimensionIndex" });
                long? dimId = null;
                if (searchResult.TryGetProperty("values", out var dims))
                {
                    foreach (var dim in dims.EnumerateArray())
                    {
                        if (dim.TryGetProperty("dimensionName", out var dn)
                            && string.Equals(dn.GetString(), dimName, StringComparison.OrdinalIgnoreCase))
                        {
                            dimensionIndex = dim.GetProperty("dimensionIndex").GetInt32();
                            dimId = dim.GetProperty("id").GetInt64();
                            break;
                        }
                    }
                }

                if (dimensionIndex == null)
                {
                    var dimResult = await api.PostAsync("/ledger/accountingDimensionName",
                        new { dimensionName = dimName });
                    dimensionIndex = dimResult.GetProperty("value").GetProperty("dimensionIndex").GetInt32();
                    _logger.LogInformation("Created dimension '{Name}' at index {Index}", dimName, dimensionIndex);
                }
                else
                {
                    _logger.LogInformation("Found existing dimension '{Name}' at index {Index}", dimName, dimensionIndex);
                }

                // Extract values to create
                var values = ExtractStringList(dimensionEntity, "values");
                // Fallback: singular "value" key
                if (values.Count == 0)
                    values = ExtractStringList(dimensionEntity, "value");

                // Determine which value to link to the posting
                var linkedValue = GetStringField(voucher, "dimensionValue")
                    ?? GetStringField(voucher, "linked_dimension_value")
                    ?? GetStringField(dimensionEntity, "value");

                // Ensure linked value is in the creation list
                if (linkedValue != null && !values.Any(v => string.Equals(v, linkedValue, StringComparison.OrdinalIgnoreCase)))
                    values.Add(linkedValue);

                // Also check nested dimension object in voucher entity
                if (linkedValue == null && voucher.TryGetValue("dimension", out var dimObj2) && dimObj2 is JsonElement dimJe2
                    && dimJe2.ValueKind == JsonValueKind.Object)
                {
                    if (dimJe2.TryGetProperty("value", out var dvProp))
                        linkedValue = dvProp.GetString();
                }

                // Search for existing dimension values
                var existingValues = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var valSearch = await api.GetAsync("/ledger/accountingDimensionValue/search",
                    new Dictionary<string, string> { ["dimensionIndex"] = dimensionIndex.Value.ToString(), ["count"] = "100", ["fields"] = "id,displayName" });
                if (valSearch.TryGetProperty("values", out var existingVals))
                {
                    foreach (var ev in existingVals.EnumerateArray())
                    {
                        if (ev.TryGetProperty("displayName", out var dn))
                            existingValues[dn.GetString()!] = ev.GetProperty("id").GetInt64();
                    }
                }

                foreach (var val in values)
                {
                    long valId;
                    if (existingValues.TryGetValue(val, out var existingId))
                    {
                        valId = existingId;
                        _logger.LogInformation("Found existing dimension value '{Value}' with ID {Id}", val, valId);
                    }
                    else
                    {
                        var valResult = await api.PostAsync("/ledger/accountingDimensionValue",
                            new { displayName = val, dimensionIndex = dimensionIndex.Value, active = true, showInVoucherRegistration = true });
                        valId = valResult.GetProperty("value").GetProperty("id").GetInt64();
                        _logger.LogInformation("Created dimension value '{Value}' with ID {Id}", val, valId);
                    }

                    if (string.Equals(val, linkedValue, StringComparison.OrdinalIgnoreCase))
                        linkedDimensionValueId = valId;
                }
            }
        }

        // --- Step 2: Determine date & description ---
        var date = GetStringField(voucher, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd"));
        var description = GetStringField(voucher, "description") ?? "Bilag";

        // --- Step 3: Build postings ---
        var postings = new List<Dictionary<string, object>>();

        // Try structured postings nested in voucher entity
        if (voucher.TryGetValue("postings", out var pVal) && pVal is JsonElement pArr && pArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pArr.EnumerateArray())
                postings.Add(await BuildPostingFromJson(api, item, date));
        }

        // Try separate postings entity
        var postingsEntity = extracted.Entities.GetValueOrDefault("postings") ?? new();
        foreach (var (_, val) in postingsEntity)
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Object)
                postings.Add(await BuildPostingFromJson(api, je, date));
        }

        // Try debit/credit account pair
        if (postings.Count == 0)
        {
            var debitAccount = GetStringField(voucher, "debitAccount") ?? GetStringField(voucher, "debit_account");
            var creditAccount = GetStringField(voucher, "creditAccount") ?? GetStringField(voucher, "credit_account");
            decimal amount = ExtractAmount(voucher, extracted);

            if (debitAccount != null && creditAccount != null && amount != 0)
            {
                var (debitId, debitVatId, _) = await ResolveAccountId(api, debitAccount);
                var (creditId, creditVatId, _) = await ResolveAccountId(api, creditAccount);
                if (debitId.HasValue && creditId.HasValue)
                {
                    postings.Add(new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = debitId.Value },
                        ["amountGross"] = amount,
                        ["amountGrossCurrency"] = amount
                    });
                    if (debitVatId.HasValue) postings[^1]["vatType"] = new { id = debitVatId.Value };
                    postings.Add(new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = creditId.Value },
                        ["amountGross"] = -amount,
                        ["amountGrossCurrency"] = -amount
                    });
                    if (creditVatId.HasValue) postings[^1]["vatType"] = new { id = creditVatId.Value };
                }
                else
                {
                    _logger.LogWarning("Skipping voucher: debit={Debit}(found={DF}) credit={Credit}(found={CF})",
                        debitAccount, debitId.HasValue, creditAccount, creditId.HasValue);
                }
            }
            else if (debitAccount != null && amount != 0)
            {
                var (debitId, debitVatId, _) = await ResolveAccountId(api, debitAccount);
                if (debitId.HasValue)
                {
                    postings.Add(new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = debitId.Value },
                        ["amountGross"] = amount,
                        ["amountGrossCurrency"] = amount
                    });
                    if (debitVatId.HasValue) postings[^1]["vatType"] = new { id = debitVatId.Value };
                }
            }
        }

        // Fallback: single account + amount → auto-generate counter-posting
        if (postings.Count == 0)
        {
            var account = NormalizeAccountNumber(
                GetStringField(voucher, "account") ?? GetStringField(voucher, "accountNumber"),
                GetStringField(voucher, "description") ?? description);
            decimal amount = ExtractAmount(voucher, extracted);

            if (account != null && amount != 0)
            {
                var (accountId, vatId, _) = await ResolveAccountId(api, account);
                if (accountId.HasValue)
                {
                    var debitPosting = new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = accountId.Value },
                        ["amountGross"] = amount,
                        ["amountGrossCurrency"] = amount
                    };
                    if (vatId.HasValue) debitPosting["vatType"] = new { id = vatId.Value };
                    postings.Add(debitPosting);

                    // Counter-account: 1920 (bank) as safe default
                    var (counterId, _, _) = await ResolveAccountId(api, "1920");
                    if (counterId.HasValue)
                    {
                        postings.Add(new Dictionary<string, object>
                        {
                            ["date"] = date,
                            ["description"] = description,
                            ["account"] = new { id = counterId.Value },
                            ["amountGross"] = -amount,
                            ["amountGrossCurrency"] = -amount
                        });
                    }
                }
            }
        }

        // Assign row numbers and link dimension values to first (debit) posting
        for (int i = 0; i < postings.Count; i++)
        {
            postings[i]["row"] = i + 1;
            if (i == 0 && linkedDimensionValueId.HasValue && dimensionIndex.HasValue)
                postings[i][$"freeAccountingDimension{dimensionIndex.Value}"] = new { id = linkedDimensionValueId.Value };
        }

        _logger.LogInformation("Creating voucher: {Description} with {PostingCount} postings", description, postings.Count);

        var body = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["postings"] = postings
        };

        var result = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
        var voucherId = result.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created voucher ID: {Id}", voucherId);
        return new HandlerResult { EntityType = "voucher", EntityId = voucherId };
    }

    private async Task<Dictionary<string, object>> BuildPostingFromJson(TripletexApiClient api, JsonElement item, string date, double? resolvedComputedAmount = null)
    {
        var posting = new Dictionary<string, object> { ["date"] = date };
        if (item.TryGetProperty("description", out var d)) posting["description"] = d.GetString()!;

        string? accountStr = null;
        if (item.TryGetProperty("accountNumber", out var an))
            accountStr = an.ValueKind == JsonValueKind.Number ? an.GetInt64().ToString() : an.GetString()!;
        else if (item.TryGetProperty("account", out var acc))
            accountStr = acc.ValueKind == JsonValueKind.Number ? acc.GetInt64().ToString() : acc.GetString()!;

        if (accountStr != null && accountStr.Length <= 4)
        {
            var (accId, vatId, vatLocked) = await ResolveAccountId(api, accountStr);
            if (accId.HasValue) posting["account"] = new { id = accId.Value };
            // vatId is null when account is locked to VAT 0 (no VAT) — ResolveAccountId filters it
            if (vatId.HasValue) posting["vatType"] = new { id = vatId.Value };
        }

        double? rawAmount = null;
        if (item.TryGetProperty("amountGross", out var ag))
        {
            if (ag.ValueKind == JsonValueKind.Number)
                rawAmount = ag.GetDouble();
            else if (ag.ValueKind == JsonValueKind.String && double.TryParse(ag.GetString()!, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                rawAmount = parsed;
        }
        else if (item.TryGetProperty("amount", out var am))
        {
            if (am.ValueKind == JsonValueKind.Number)
                rawAmount = am.GetDouble();
            else if (am.ValueKind == JsonValueKind.String && double.TryParse(am.GetString()!, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed2))
                rawAmount = parsed2;
            else if (am.ValueKind == JsonValueKind.String && resolvedComputedAmount.HasValue)
                rawAmount = resolvedComputedAmount.Value;
        }

        if (rawAmount.HasValue)
        {
            // Apply debitCredit sign: "credit" negates the amount
            if (item.TryGetProperty("debitCredit", out var dc) && dc.ValueKind == JsonValueKind.String
                && string.Equals(dc.GetString(), "credit", StringComparison.OrdinalIgnoreCase))
                rawAmount = -Math.Abs(rawAmount.Value);

            posting["amountGross"] = rawAmount.Value;
            posting["amountGrossCurrency"] = rawAmount.Value;
        }

        return posting;
    }

    private decimal ExtractAmount(Dictionary<string, object> voucher, ExtractionResult extracted)
    {
        var amountStr = GetStringField(voucher, "amount")
            ?? GetStringField(voucher, "amountGross")
            ?? GetStringField(voucher, "debit_amount")
            ?? GetStringField(voucher, "debitAmount");

        if (amountStr != null && decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            return amount;

        if (extracted.RawAmounts.Count > 0 && decimal.TryParse(extracted.RawAmounts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var rawAmount))
            return rawAmount;

        return 0m;
    }

    private static List<string> ExtractStringList(Dictionary<string, object> dict, string key)
    {
        var result = new List<string>();
        if (dict.TryGetValue(key, out var val) && val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                foreach (var item in je.EnumerateArray()) result.Add(item.GetString()!);
            else if (je.ValueKind == JsonValueKind.String)
                result.Add(je.GetString()!);
        }
        return result;
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
                // Use the actual vatLocked field from the API
                bool locked = v.TryGetProperty("vatLocked", out var vl) && vl.ValueKind == JsonValueKind.True;
                if (v.TryGetProperty("vatType", out var vt) && vt.ValueKind == JsonValueKind.Object)
                {
                    if (vt.TryGetProperty("id", out var vtId) && vtId.ValueKind == JsonValueKind.Number)
                    {
                        var rawId = vtId.GetInt64();
                        int vatNumber = 0;
                        if (vt.TryGetProperty("number", out var vtNum) && vtNum.ValueKind == JsonValueKind.Number)
                            vatNumber = vtNum.GetInt32();
                        // Only inherit VAT type when account is LOCKED to it.
                        // vatLocked=false means flexible/exempt VAT — don't force a default rate.
                        // Accounts like 7100 (Bilgodtgjørelse) reject vatType even when their
                        // default vatType is non-zero ("ikke aktivert for moms" 422 error).
                        if (locked && vatNumber != 0 && rawId > 0)
                            vatId = rawId;
                    }
                }
                return (id, vatId, locked);
            }
        }
        _logger.LogWarning("Account {Number} not found in chart of accounts", accountNumber);
        return (null, null, false);
    }

    private async Task<HandlerResult> HandleMultiVoucher(TripletexApiClient api, ExtractionResult extracted,
        List<KeyValuePair<string, Dictionary<string, object>>> numberedVouchers)
    {
        _logger.LogInformation("Processing {Count} numbered vouchers", numberedVouchers.Count);
        long firstVoucherId = 0;
        int created = 0;

        foreach (var (key, voucherEntity) in numberedVouchers)
        {
            var date = GetStringField(voucherEntity, "date")
                ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd"));
            var description = GetStringField(voucherEntity, "description") ?? "Bilag";

            var postings = new List<Dictionary<string, object>>();

            // Path 1: Structured postings array inside the voucher entity
            if (voucherEntity.TryGetValue("postings", out var pVal) && pVal is JsonElement pArr && pArr.ValueKind == JsonValueKind.Array)
            {
                // Check if any posting has a computed (non-numeric) amount
                double? resolvedComputed = null;
                foreach (var item in pArr.EnumerateArray())
                {
                    var amtProp = item.TryGetProperty("amount", out var amCheck) ? amCheck
                        : item.TryGetProperty("amountGross", out var agCheck) ? agCheck
                        : default;
                    if (amtProp.ValueKind == JsonValueKind.String)
                    {
                        var amtStr = amtProp.GetString();
                        if (amtStr != null && !double.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        {
                            _logger.LogInformation("Detected computed amount '{Amt}' in {Key} — resolving via ledger", amtStr, key);
                            resolvedComputed ??= await ResolveComputedTaxAmountAsync(api, date);
                            break;
                        }
                    }
                }

                foreach (var item in pArr.EnumerateArray())
                {
                    var p = await BuildPostingFromJson(api, item, date, resolvedComputed);
                    if (p.ContainsKey("amountGross"))
                        postings.Add(p);
                    else
                        _logger.LogWarning("Skipping posting in {Key} — non-numeric or missing amount", key);
                }
            }

            // Path 2: debitAccount/creditAccount pair
            if (postings.Count == 0)
            {
                var debitAccount = GetStringField(voucherEntity, "debitAccount") ?? GetStringField(voucherEntity, "debit_account");
                var creditAccount = GetStringField(voucherEntity, "creditAccount") ?? GetStringField(voucherEntity, "credit_account");
                decimal amount = ExtractAmount(voucherEntity, extracted);

                if (debitAccount != null && creditAccount != null && amount != 0)
                {
                    // Both accounts specified — resolve both and only create if both exist
                    var (debitId, debitVatId, _) = await ResolveAccountId(api, debitAccount);
                    var (creditId, creditVatId, _) = await ResolveAccountId(api, creditAccount);
                    if (debitId.HasValue && creditId.HasValue)
                    {
                        postings.Add(new Dictionary<string, object>
                        {
                            ["date"] = date,
                            ["description"] = description,
                            ["account"] = new { id = debitId.Value },
                            ["amountGross"] = amount,
                            ["amountGrossCurrency"] = amount
                        });
                        if (debitVatId.HasValue) postings[^1]["vatType"] = new { id = debitVatId.Value };
                        postings.Add(new Dictionary<string, object>
                        {
                            ["date"] = date,
                            ["description"] = description,
                            ["account"] = new { id = creditId.Value },
                            ["amountGross"] = -amount,
                            ["amountGrossCurrency"] = -amount
                        });
                        if (creditVatId.HasValue) postings[^1]["vatType"] = new { id = creditVatId.Value };
                    }
                    else
                    {
                        _logger.LogWarning("Skipping {Key}: debit={Debit}(found={DF}) credit={Credit}(found={CF})",
                            key, debitAccount, debitId.HasValue, creditAccount, creditId.HasValue);
                    }
                }
                else if (debitAccount != null && amount != 0)
                {
                    // Only debit account — will fall through to Path 3 if this also fails
                    var (debitId, debitVatId, _) = await ResolveAccountId(api, debitAccount);
                    if (debitId.HasValue)
                    {
                        postings.Add(new Dictionary<string, object>
                        {
                            ["date"] = date,
                            ["description"] = description,
                            ["account"] = new { id = debitId.Value },
                            ["amountGross"] = amount,
                            ["amountGrossCurrency"] = amount
                        });
                        if (debitVatId.HasValue) postings[^1]["vatType"] = new { id = debitVatId.Value };
                    }
                }
            }

            // Path 3: single account + auto counter-posting
            if (postings.Count == 0)
            {
                var account = NormalizeAccountNumber(
                    GetStringField(voucherEntity, "account") ?? GetStringField(voucherEntity, "accountNumber"),
                    GetStringField(voucherEntity, "description") ?? description);
                decimal amount = ExtractAmount(voucherEntity, extracted);
                if (account != null && amount != 0)
                {
                    var (accountId, vatId, _) = await ResolveAccountId(api, account);
                    if (accountId.HasValue)
                    {
                        var debitP = new Dictionary<string, object>
                        {
                            ["date"] = date,
                            ["description"] = description,
                            ["account"] = new { id = accountId.Value },
                            ["amountGross"] = amount,
                            ["amountGrossCurrency"] = amount
                        };
                        if (vatId.HasValue) debitP["vatType"] = new { id = vatId.Value };
                        postings.Add(debitP);

                        var (counterId, _, _) = await ResolveAccountId(api, "1920");
                        if (counterId.HasValue)
                            postings.Add(new Dictionary<string, object>
                            {
                                ["date"] = date,
                                ["description"] = description,
                                ["account"] = new { id = counterId.Value },
                                ["amountGross"] = -amount,
                                ["amountGrossCurrency"] = -amount
                            });
                    }
                }
            }

            if (postings.Count == 0)
            {
                _logger.LogWarning("Skipping {Key} — no valid postings could be built", key);
                continue;
            }

            for (int i = 0; i < postings.Count; i++)
                postings[i]["row"] = i + 1;

            _logger.LogInformation("Creating voucher {Key}: {Description} with {PostingCount} postings",
                key, description, postings.Count);

            try
            {
                var body = new Dictionary<string, object>
                {
                    ["date"] = date,
                    ["description"] = description,
                    ["postings"] = postings
                };

                var result = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
                var voucherId = result.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created voucher {Key} ID: {Id}", key, voucherId);

                if (firstVoucherId == 0) firstVoucherId = voucherId;
                created++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to create {Key}: {Msg}", key, ex.Message);
            }
        }

        _logger.LogInformation("Multi-voucher complete: {Created}/{Total} vouchers created", created, numberedVouchers.Count);
        return new HandlerResult { EntityType = "voucher", EntityId = firstVoucherId };
    }

    private async Task<HandlerResult> HandleSupplierInvoice(TripletexApiClient api, ExtractionResult extracted, Dictionary<string, object> voucher)
    {
        var supplierName = GetStringField(voucher, "supplierName");
        var supplierOrgNumber = GetStringField(voucher, "supplierOrgNumber");
        var invoiceNumber = GetStringField(voucher, "invoiceNumber");
        var description = GetStringField(voucher, "description") ?? invoiceNumber ?? "Leverandørfaktura";
        var rawAccount = GetStringField(voucher, "account") ?? GetStringField(voucher, "accountNumber");
        var account = NormalizeAccountNumber(rawAccount, $"{description} {supplierName} {invoiceNumber}") ?? "6500";
        decimal amount = ExtractAmount(voucher, extracted);
        var date = GetStringField(voucher, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd"));

        if (!string.Equals(rawAccount, account, StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("Normalized non-numeric supplier voucher account '{RawAccount}' to '{Account}'", rawAccount, account);

        // 1. Create supplier
        var supplierBody = new Dictionary<string, object> { ["name"] = supplierName! };
        if (supplierOrgNumber != null) supplierBody["organizationNumber"] = supplierOrgNumber;
        var email = GetStringField(voucher, "supplierEmail") ?? GetStringField(voucher, "email");
        if (email != null) supplierBody["email"] = email;

        var supplierResult = await api.PostAsync("/supplier", supplierBody);
        var supplierId = supplierResult.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created supplier '{Name}' ID: {Id}", supplierName, supplierId);

        // 2. Resolve optional department for receipt-style vouchers.
        var departmentId = await ResolveDepartmentIdAsync(api, extracted, voucher);

        // 3. Resolve expense account (with vatLocked check)
        var (accountId, lockedVatId, acctVatLocked) = await ResolveAccountId(api, account);
        if (!accountId.HasValue && account != "6800")
        {
            _logger.LogWarning("Primary expense account {Account} was not found, falling back to 6800", account);
            account = "6800";
            (accountId, lockedVatId, acctVatLocked) = await ResolveAccountId(api, account);
        }

        if (!accountId.HasValue)
            throw new InvalidOperationException($"Unable to resolve expense account for supplier voucher (raw='{rawAccount ?? "<null>"}', normalized='{account}').");

        // 4. Resolve VAT type — respect account lock
        long? inputVatId = null;
        if (acctVatLocked && lockedVatId.HasValue)
        {
            inputVatId = lockedVatId;
        }
        else if (!acctVatLocked)
        {
            var vatRate = GetStringField(voucher, "vatRate") ?? GetStringField(voucher, "vatPercentage");
            var vatNumber = "1"; // Default: inbound 25%
            if (vatRate != null)
            {
                if (vatRate.Contains("15")) vatNumber = "11";
                else if (vatRate.Contains("12")) vatNumber = "13";
            }
            var vatResult = await api.GetAsync("/ledger/vatType", new Dictionary<string, string>
            {
                ["number"] = vatNumber,
                ["count"] = "1",
                ["fields"] = "id"
            });
            if (vatResult.TryGetProperty("values", out var vatVals))
                foreach (var vv in vatVals.EnumerateArray())
                {
                    inputVatId = vv.GetProperty("id").GetInt64();
                    break;
                }
        }

        // 5. POST /supplierInvoice — creates a real supplierInvoice entity (not just a voucher)
        var (creditorId, _, _) = await ResolveAccountId(api, "2400");
        if (!creditorId.HasValue)
            throw new InvalidOperationException("Unable to resolve creditor account 2400 for supplier voucher.");

        // Calculate due date (invoice date + 30 days)
        var dueDate = DateTime.TryParse(date, out var parsedDate)
            ? parsedDate.AddDays(30).ToString("yyyy-MM-dd")
            : DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");

        var debitPosting = new Dictionary<string, object>
        {
            ["date"] = date,
            ["amountGross"] = amount,
            ["amountGrossCurrency"] = amount,
            ["account"] = new Dictionary<string, object> { ["id"] = accountId.Value },
            ["row"] = 1
        };
        if (inputVatId.HasValue) debitPosting["vatType"] = new Dictionary<string, object> { ["id"] = inputVatId.Value };
        if (departmentId.HasValue) debitPosting["department"] = new Dictionary<string, object> { ["id"] = departmentId.Value };

        var creditPosting = new Dictionary<string, object>
        {
            ["date"] = date,
            ["amountGross"] = -amount,
            ["amountGrossCurrency"] = -amount,
            ["account"] = new Dictionary<string, object> { ["id"] = creditorId.Value },
            ["supplier"] = new Dictionary<string, object> { ["id"] = supplierId },
            ["row"] = 2
        };

        var supplierInvoiceBody = new Dictionary<string, object>
        {
            ["invoiceNumber"] = invoiceNumber ?? "INV-0000",
            ["invoiceDate"] = date,
            ["invoiceDueDate"] = dueDate,
            ["supplier"] = new Dictionary<string, object> { ["id"] = supplierId },
            ["voucher"] = new Dictionary<string, object>
            {
                ["date"] = date,
                ["description"] = description,
                ["postings"] = new[] { debitPosting, creditPosting }
            }
        };

        _logger.LogInformation("Creating supplier invoice via POST /supplierInvoice: {Description} amount={Amount}", description, amount);

        var siResult = await api.PostAsync("/supplierInvoice", supplierInvoiceBody);
        var siId = siResult.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created supplier invoice ID: {Id}", siId);
        return new HandlerResult { EntityType = "supplierInvoice", EntityId = siId };
    }

    /// <summary>
    /// Resolves computed tax amount by querying the ledger for P&L result and applying 22% Norwegian corporate tax rate.
    /// Queries accounts 3000-8699 (operating + financial result, before tax expense on 8700).
    /// </summary>
    private async Task<double> ResolveComputedTaxAmountAsync(TripletexApiClient api, string date, double taxRate = 0.22)
    {
        var year = date.Length >= 4 ? date[..4] : DateTime.Now.Year.ToString();
        var dateFrom = $"{year}-01-01";
        var dateTo = $"{int.Parse(year) + 1}-01-01"; // exclusive end

        var result = await api.GetAsync("/ledger/posting", new Dictionary<string, string>
        {
            ["dateFrom"] = dateFrom,
            ["dateTo"] = dateTo,
            ["accountNumberFrom"] = "3000",
            ["accountNumberTo"] = "8699",
            ["count"] = "10000",
            ["fields"] = "amount"
        });

        double totalPnL = 0;
        if (result.TryGetProperty("values", out var values))
        {
            foreach (var v in values.EnumerateArray())
            {
                if (v.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number)
                    totalPnL += amt.GetDouble();
            }
        }

        // In Tripletex: income is credit (negative), expense is debit (positive)
        // Taxable result = -(sum of P&L postings) → positive when profitable
        var taxableResult = -totalPnL;
        var taxCost = taxableResult > 0 ? Math.Round(taxableResult * taxRate, 2) : 0;

        _logger.LogInformation("Tax calculation: P&L sum={PnL:F2}, taxable result={Taxable:F2}, tax ({Rate}%)={Tax:F2}",
            totalPnL, taxableResult, taxRate * 100, taxCost);

        return taxCost;
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

    private async Task<long?> ResolveDepartmentIdAsync(TripletexApiClient api, ExtractionResult extracted, Dictionary<string, object> voucher)
    {
        var departmentName = GetStringField(extracted.Entities.GetValueOrDefault("department") ?? new(), "name");

        if (departmentName == null)
        {
            var dimension = extracted.Entities.GetValueOrDefault("dimension") ?? new();
            var dimensionName = GetStringField(dimension, "name");
            if (dimensionName != null && dimensionName.Contains("department", StringComparison.OrdinalIgnoreCase))
            {
                departmentName = GetStringField(voucher, "dimensionValue");
                if (departmentName == null)
                {
                    var values = ExtractStringList(dimension, "values");
                    if (values.Count > 0)
                        departmentName = values[0];
                }
            }
        }

        if (string.IsNullOrWhiteSpace(departmentName))
            return null;

        var result = await api.GetAsync("/department", new Dictionary<string, string>
        {
            ["from"] = "0",
            ["count"] = "1000",
            ["fields"] = "id,name,departmentNumber"
        });

        var nextDepartmentNumber = 1;
        if (result.TryGetProperty("values", out var valuesElement))
        {
            foreach (var candidate in valuesElement.EnumerateArray())
            {
                if (candidate.TryGetProperty("departmentNumber", out var departmentNumberProp)
                    && departmentNumberProp.ValueKind != JsonValueKind.Null)
                {
                    int parsedDepartmentNumber;
                    var departmentNumberText = departmentNumberProp.ValueKind == JsonValueKind.Number
                        ? departmentNumberProp.GetInt32().ToString()
                        : departmentNumberProp.GetString();

                    if (int.TryParse(departmentNumberText, out parsedDepartmentNumber))
                        nextDepartmentNumber = Math.Max(nextDepartmentNumber, parsedDepartmentNumber + 1);
                }

                if (candidate.TryGetProperty("name", out var nameProp)
                    && string.Equals(nameProp.GetString(), departmentName, StringComparison.OrdinalIgnoreCase)
                    && candidate.TryGetProperty("id", out var idProp))
                {
                    var departmentId = idProp.GetInt64();
                    _logger.LogInformation("Resolved department '{Department}' to ID {Id} for supplier voucher", departmentName, departmentId);
                    return departmentId;
                }
            }
        }

        _logger.LogInformation("Department '{Department}' was extracted for supplier voucher but was not found; creating it with departmentNumber {Number}",
            departmentName, nextDepartmentNumber);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var createResult = await api.PostAsync("/department", new Dictionary<string, object>
                {
                    ["name"] = departmentName,
                    ["departmentNumber"] = nextDepartmentNumber
                });

                var createdDepartmentId = createResult.GetProperty("value").GetProperty("id").GetInt64();
                _logger.LogInformation("Created department '{Department}' with ID {Id} for supplier voucher", departmentName, createdDepartmentId);
                return createdDepartmentId;
            }
            catch (TripletexApiException ex) when (ex.Message.Contains("Nummeret er i bruk") || ex.Message.Contains("nummer") || ex.Message.Contains("duplicate"))
            {
                _logger.LogWarning("Department number {Number} was already in use while creating '{Department}', trying next", nextDepartmentNumber, departmentName);
                nextDepartmentNumber++;
            }
        }

        throw new InvalidOperationException($"Unable to create department '{departmentName}' for supplier voucher after multiple attempts.");
    }

    private static string? NormalizeAccountNumber(string? account, string? description)
    {
        if (string.IsNullOrWhiteSpace(account))
            return null;

        account = account.Trim();

        if (account.All(char.IsDigit))
            return account;

        var text = $"{account} {description}";
        if (text.Contains("headset", StringComparison.OrdinalIgnoreCase)
            || text.Contains("it-utstyr", StringComparison.OrdinalIgnoreCase)
            || text.Contains("office equipment", StringComparison.OrdinalIgnoreCase))
            return "6800";

        if (text.Contains("tog", StringComparison.OrdinalIgnoreCase)
            || text.Contains("train", StringComparison.OrdinalIgnoreCase)
            || text.Contains("transport", StringComparison.OrdinalIgnoreCase)
            || text.Contains("reise", StringComparison.OrdinalIgnoreCase)
            || text.Contains("travel", StringComparison.OrdinalIgnoreCase))
            return "7140";

        if (text.Contains("kaffe", StringComparison.OrdinalIgnoreCase)
            || text.Contains("coffee", StringComparison.OrdinalIgnoreCase)
            || text.Contains("møte", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mote", StringComparison.OrdinalIgnoreCase)
            || text.Contains("meeting", StringComparison.OrdinalIgnoreCase))
            return "7140";

        return "6000";
    }
}
