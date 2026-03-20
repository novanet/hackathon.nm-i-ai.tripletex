using System.Globalization;
using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class VoucherHandler : ITaskHandler
{
    private readonly ILogger<VoucherHandler> _logger;

    public VoucherHandler(ILogger<VoucherHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var voucher = extracted.Entities.GetValueOrDefault("voucher") ?? new();

        // --- Supplier invoice detection ---
        var supplierName = GetStringField(voucher, "supplierName");
        var supplierOrgNumber = GetStringField(voucher, "supplierOrgNumber");
        if (supplierName != null || supplierOrgNumber != null)
            return await HandleSupplierInvoice(api, extracted, voucher);

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

            if (debitAccount != null && amount != 0)
            {
                var (debitId, debitVatId, _) = await ResolveAccountId(api, debitAccount);
                if (debitId.HasValue)
                {
                    var p = new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = debitId.Value },
                        ["amountGross"] = amount,
                        ["amountGrossCurrency"] = amount
                    };
                    if (debitVatId.HasValue) p["vatType"] = new { id = debitVatId.Value };
                    postings.Add(p);
                }
            }
            if (creditAccount != null && amount != 0)
            {
                var (creditId, creditVatId, _) = await ResolveAccountId(api, creditAccount);
                if (creditId.HasValue)
                {
                    var p = new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["description"] = description,
                        ["account"] = new { id = creditId.Value },
                        ["amountGross"] = -amount,
                        ["amountGrossCurrency"] = -amount
                    };
                    if (creditVatId.HasValue) p["vatType"] = new { id = creditVatId.Value };
                    postings.Add(p);
                }
            }
        }

        // Fallback: single account + amount → auto-generate counter-posting
        if (postings.Count == 0)
        {
            var account = GetStringField(voucher, "account") ?? GetStringField(voucher, "accountNumber");
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

    private async Task<Dictionary<string, object>> BuildPostingFromJson(TripletexApiClient api, JsonElement item, string date)
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
            var (accId, vatId, _) = await ResolveAccountId(api, accountStr);
            if (accId.HasValue) posting["account"] = new { id = accId.Value };
            if (vatId.HasValue) posting["vatType"] = new { id = vatId.Value };
        }

        if (item.TryGetProperty("amountGross", out var ag))
        {
            var val = ag.ValueKind == JsonValueKind.Number ? ag.GetDouble() : double.Parse(ag.GetString()!, CultureInfo.InvariantCulture);
            posting["amountGross"] = val;
            posting["amountGrossCurrency"] = val;
        }
        else if (item.TryGetProperty("amount", out var am))
        {
            var val = am.ValueKind == JsonValueKind.Number ? am.GetDouble() : double.Parse(am.GetString()!, CultureInfo.InvariantCulture);
            posting["amountGross"] = val;
            posting["amountGrossCurrency"] = val;
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
                        if (vatNumber != 0 && rawId > 0)
                            vatId = rawId;
                    }
                }
                return (id, vatId, locked);
            }
        }
        return (null, null, false);
    }

    private async Task<HandlerResult> HandleSupplierInvoice(TripletexApiClient api, ExtractionResult extracted, Dictionary<string, object> voucher)
    {
        var supplierName = GetStringField(voucher, "supplierName");
        var supplierOrgNumber = GetStringField(voucher, "supplierOrgNumber");
        var invoiceNumber = GetStringField(voucher, "invoiceNumber");
        var description = GetStringField(voucher, "description") ?? invoiceNumber ?? "Leverandørfaktura";
        var account = GetStringField(voucher, "account") ?? GetStringField(voucher, "accountNumber") ?? "6500";
        decimal amount = ExtractAmount(voucher, extracted);
        var date = GetStringField(voucher, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd"));

        // 1. Create supplier
        var supplierBody = new Dictionary<string, object> { ["name"] = supplierName! };
        if (supplierOrgNumber != null) supplierBody["organizationNumber"] = supplierOrgNumber;
        var email = GetStringField(voucher, "supplierEmail") ?? GetStringField(voucher, "email");
        if (email != null) supplierBody["email"] = email;

        var supplierResult = await api.PostAsync("/supplier", supplierBody);
        var supplierId = supplierResult.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created supplier '{Name}' ID: {Id}", supplierName, supplierId);

        // 2. Look up Leverandørfaktura voucher type
        var voucherTypes = await api.GetAsync("/ledger/voucherType",
            new Dictionary<string, string> { ["name"] = "Leverandørfaktura", ["count"] = "10", ["fields"] = "id,name" });
        long? voucherTypeId = null;
        if (voucherTypes.TryGetProperty("values", out var vtVals))
        {
            foreach (var vt in vtVals.EnumerateArray())
            {
                voucherTypeId = vt.GetProperty("id").GetInt64();
                break;
            }
        }

        // 3. Resolve expense account (use its locked vatType if present)
        var (accountId, lockedVatId, acctVatLocked) = await ResolveAccountId(api, account);

        // 4. Resolve creditor account (2400 = leverandørgjeld)
        var (creditorId, _, _) = await ResolveAccountId(api, "2400");

        // 5. Resolve VAT type — respect account lock
        long? inputVatId = null;
        if (acctVatLocked && lockedVatId.HasValue)
        {
            // Account locked to a specific non-zero VAT type — use it
            inputVatId = lockedVatId;
        }
        else if (acctVatLocked && !lockedVatId.HasValue)
        {
            // Account locked to VAT code 0 (no VAT) — don't set vatType
            inputVatId = null;
        }
        else
        {
            // No lock — look up the correct input VAT type
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
            {
                foreach (var vv in vatVals.EnumerateArray())
                {
                    inputVatId = vv.GetProperty("id").GetInt64();
                    break;
                }
            }
        }

        // 6. Build postings
        var postings = new List<Dictionary<string, object>>();

        // Debit posting: expense account with INPUT VAT
        var debitPosting = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["account"] = new { id = accountId!.Value },
            ["amountGross"] = amount,
            ["amountGrossCurrency"] = amount,
            ["supplier"] = new { id = supplierId },
            ["row"] = 1
        };
        if (inputVatId.HasValue) debitPosting["vatType"] = new { id = inputVatId.Value };
        if (invoiceNumber != null) debitPosting["invoiceNumber"] = invoiceNumber;
        postings.Add(debitPosting);

        // Credit posting: creditor account
        var creditPosting = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["account"] = new { id = creditorId!.Value },
            ["amountGross"] = -amount,
            ["amountGrossCurrency"] = -amount,
            ["supplier"] = new { id = supplierId },
            ["row"] = 2
        };
        if (invoiceNumber != null) creditPosting["invoiceNumber"] = invoiceNumber;
        postings.Add(creditPosting);

        _logger.LogInformation("Creating supplier invoice voucher: {Description} amount={Amount} supplier={Supplier} inputVatId={VatId}", description, amount, supplierName, inputVatId);

        // 7. Create voucher
        var body = new Dictionary<string, object>
        {
            ["date"] = date,
            ["description"] = description,
            ["postings"] = postings
        };
        if (voucherTypeId.HasValue) body["voucherType"] = new { id = voucherTypeId.Value };
        if (invoiceNumber != null) body["vendorInvoiceNumber"] = invoiceNumber;

        var result = await api.PostAsync("/ledger/voucher?sendToLedger=true", body);
        var voucherId = result.GetProperty("value").GetProperty("id").GetInt64();
        _logger.LogInformation("Created supplier invoice voucher ID: {Id}", voucherId);
        return new HandlerResult { EntityType = "voucher", EntityId = voucherId };
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
