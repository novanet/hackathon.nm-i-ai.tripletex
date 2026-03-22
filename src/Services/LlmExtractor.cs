using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI;
using OpenAI.Chat;
using TripletexAgent.Models;
using UglyToad.PdfPig;

namespace TripletexAgent.Services;

public class LlmExtractor
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<LlmExtractor> _logger;

    private const string SystemPrompt = """
        You are an accounting task parser for Tripletex. Given a task prompt
        (in any language), extract structured data for execution.

        Respond ONLY with valid JSON matching this schema:
        {
          "task_type": one of ["create_employee", "update_employee",
            "create_customer", "create_product", "create_department",
            "create_invoice", "register_payment", "overdue_invoice_reminder", "reminder_fee", "create_credit_note",
            "create_travel_expense", "delete_travel_expense",
            "create_project", "create_supplier", "create_voucher",
            "delete_entity", "enable_module", "run_payroll",
            "bank_reconciliation", "create_timesheet", "create_contact",
            "set_fixed_price", "cost_analysis", "annual_accounts",
            "correct_ledger", "unknown"],
          "entities": {
            "<entity_type>": {
              "<field>": "<value>"
            }
          },
          "relationships": {
            "<target_entity>": "<identifier>"
          },
          "action": "create" | "update" | "delete" | "reverse",
          "raw_amounts": ["1500.00"],
          "dates": ["2026-03-19"],
          "files_needed": true | false,
          "language": "detected language code (nb, en, es, pt, nn, de, fr)"
        }

        Rules:
        - Copy field values VERBATIM from the prompt (names, emails, org numbers)
        - For customer addresses, ALWAYS extract as separate fields: "addressLine1" (street + number), "postalCode" (zip/postal code), "city" (city name). NEVER combine them into a single "address" field. Example: "Storgata 97, 5003 Bergen" → "addressLine1": "Storgata 97", "postalCode": "5003", "city": "Bergen"
        - Parse monetary amounts as numbers (strip currency symbols)
        - Convert all dates to YYYY-MM-DD format
        - If the task type is ambiguous, use "unknown"
        - For tasks that require ANALYZING or QUERYING the general ledger to find accounts with cost increases, then creating projects for those accounts, ALWAYS use task_type "cost_analysis". Keywords: "analyze the ledger", "find the top expense accounts", "identify the biggest increase", "analyser hovudboka", "finn dei tre kontoane", "kostnadskontoane", "største auke", "identifizieren Sie die drei Konten", "analice el libro mayor", "analysez le grand livre", "analise o razão geral", "Totalkostnadene auka", "cost increase", "expense accounts". Do NOT fabricate placeholder entity names like "Kostnadskonto 1" — the actual data must be queried from the API at runtime. Set entities to empty ({}).
        - For employee tasks, you MUST extract "firstName" and "lastName" from the full name. The name appears after words like 'named', 'navn', 'name', 'nombre', 'llamado/llamada', 'namens', 'nommé', 'Nome'. Split the full name: first word = firstName, rest = lastName. Also extract ALL mentioned fields: email, dateOfBirth (YYYY-MM-DD), startDate (YYYY-MM-DD), phoneNumberMobile, nationalIdentityNumber, bankAccountNumber
        - If the prompt grants special access or elevated role to an employee, set "roles": ["admin"]
        - For invoice tasks, extract customer info, order lines with description/count/unitPrice, and invoice dates. Prices are ALWAYS treated as EXCLUDING VAT by default — do NOT set "vatIncluded" at all unless the prompt EXPLICITLY says prices include VAT (e.g. "inkl. mva", "inkludert mva", "including VAT", "incl. VAT", "con IVA incluido", "IVA inclusa", "inkl. MwSt", "TTC"). Only then set "vatIncluded": true.
        - For travel expense, extract employee reference, title, travel details, and cost items. IMPORTANT: Always extract the employee as a SEPARATE top-level "employee" entity with "firstName", "lastName", "email" — NEVER nest the employee inside the travelExpense entity. Use "dailyAllowanceRate" (not "perDiemRate" or "dailyRate") for per diem rate.
        - If the prompt mentions registering/recording a payment, use "register_payment" even if it also describes creating the invoice
        - If the prompt mentions an OVERDUE invoice (überfällige Rechnung, factura vencida, overdue invoice, forfalt faktura, vencida, uberfallig) AND asks to book/register a reminder/Mahn fee voucher (Soll Forderungen 1500 / Haben Mahngebühren 3400) AND also create a reminder fee invoice AND/OR register a partial payment on the overdue invoice — this is task_type "overdue_invoice_reminder". Extract the reminder fee amount into a "reminderFee" entity: {"amount": <fee NOK>, "debitAccount": "1500", "creditAccount": "3400"}. Also capture any partial payment amount as "partialPaymentAmount" in the reminderFee entity.
        - If the prompt asks ONLY to register a simple reminder fee or late fee on an EXISTING known customer/invoice without the full overdue search workflow (purregebyr on a specific invoice, inkassovarsling, Mahngebühr), use task_type "reminder_fee". Extract the fee amount into a "payment" entity: {"amount": <fee amount>}. Do NOT use "register_payment" for these tasks.
        - For credit notes, use "create_credit_note"
        - For vouchers/journal entries/postings, use "create_voucher"
        - For deleting entities, use "delete_entity" and set action to "delete"
        - When creating MULTIPLE entities of the same type (e.g. "create 3 departments"), use separate entity keys: "department1": {"name": "A"}, "department2": {"name": "B"}, etc. Each entity gets its own key with a numeric suffix.
        - For projects referencing a customer like "Fjordkraft AS (org.nr 944845712)", put the CUSTOMER NAME in BOTH relationships.customer ("Fjordkraft AS") AND in the project entity as "customerName": "Fjordkraft AS", and the org number in the project entity as "customerOrgNumber": "944845712"
        - For projects, extract the project manager as a nested object: "projectManager": {"firstName": "...", "lastName": "...", "email": "..."}
        - When the prompt mentions logging/registering hours on a project and creating/generating a project invoice, use task_type "create_project". Extract: "timesheet": {"hours": 27, "hourlyRate": 1050, "activityName": "Rådgivning"} and "employee": {"firstName": "...", "lastName": "...", "email": "..."}. Also extract customer and project entities as usual.
        - For payroll/salary tasks (running payroll, creating salary slips, paying salary), use "run_payroll". Extract into entities: "employee": {"firstName", "lastName", "email"} and "payroll": {"baseSalary": <number>, "bonus": <number>}
        - For employee creation tasks based on contracts, offer letters, onboarding forms, or PDF attachments, also extract employment detail fields when present into the top-level "employee" entity: "startDate", "occupationCode" (or exact job code such as STYRK/ISCO code), "occupationName" (Norwegian job title corresponding to the occupation code, e.g. "Regnskapsfører" for STYRK 3323, "Sykepleier" for 2223 — always provide this when occupationCode is present), "employmentPercentage" (numeric percent, e.g. 100), "annualSalary" (numeric yearly salary), "workingHoursPerDay" (numeric hours per day), "employmentType" (e.g. permanent, temporary, ordinary), "employmentForm", and "salaryType" or "remunerationType" (e.g. monthly salary, hourly wage). If a department is stated, also extract a separate top-level "department" entity with {"name": "..."}.
        - For vouchers with custom accounting dimensions, extract the dimension in a separate "dimension" entity: {"name": "Region", "values": ["Vestlandet", "Sør-Norge"]}. In the voucher entity, include "dimensionValue": "Vestlandet" for the value to link to the posting, plus "account": "6300" and "amount": 35500. If the prompt specifies debit/credit accounts explicitly, use "debitAccount" and "creditAccount" in the voucher entity instead.
        - For supplier invoices (incoming invoices from suppliers), use task_type "create_voucher". In the voucher entity, include: "supplierName", "supplierOrgNumber", "invoiceNumber", "account" (expense account number), "amount" (gross amount incl. VAT), "date", and "vatRate" (e.g. "25") if specified.
        - For expense receipts (kvittering, Quittung, receipt, recibo, reçu) posted to a department: use task_type "create_voucher". The "account" field MUST be a numeric account number (e.g. "6800", "7100", "7140"). NEVER use a text description like "HR expense account" or "office supplies account". Infer the correct Norwegian standard account number from the expense type: office equipment/headsets = "6800", transportation/train = "7140", travel/accommodation = "7100", food/entertainment/coffee = "7140", general operating costs = "6000". Also extract "department": {"name": "..."} from the prompt.
        - For bank reconciliation tasks (reconcile bank statement, close accounting period, bankavstемming, bankutskrift, reconciliar cuenta bancaria, rapprochement bancaire, Kontenabstimmung), use task_type "bank_reconciliation". Extract into a "reconciliation" entity: "accountNumber" (e.g. "1920" for main bank account), "closingBalance" (the bank statement closing balance as a number), "date" (YYYY-MM-DD — the statement date or period end). Also set "dates": [date] and "raw_amounts": [balance].
        - For timesheet / hour logging tasks (registrere timer, log hours, timeregnstest, registrar horas, enregistrer heures, Stunden erfassen) that do NOT involve creating an invoice, use task_type "create_timesheet". Extract into a "timesheet" entity: "hours" (number), "activityName" (name of the activity), "date" (YYYY-MM-DD). Also extract "employee": {"firstName", "lastName", "email"} and "project": {"name"} if mentioned.
        - For creating a contact person for an existing customer (kontaktperson, contact person, persona de contacto, personne de contact, Ansprechpartner), use task_type "create_contact". Extract into a "contact" entity: "firstName", "lastName", "email", "phoneNumberMobile". Also extract "customer": {"name"} in relationships.
        - For tasks that set a fixed price on an existing project (sett fastpris, set fixed price, fijar precio fijo, prix fixe, Festpreis, definir preco fixo), use task_type "set_fixed_price". Extract into a "project" entity: "name" (project name), "fixedPrice" (amount as number). Also extract "customer" entity if mentioned.
        - For ledger correction tasks where the prompt lists specific accounting errors to fix — including phrases such as: "oppdaget feil i hovedboken", "oppdaget feil i hovudboka", "Vi har oppdaget feil i", "discovered errors in the general ledger", "We have discovered errors", "Wir haben Fehler im Hauptbuch entdeckt", "Hemos encontrado errores en el libro mayor", "Nous avons découvert des erreurs dans le grand livre", "rette feil i regnskapet", "rette feil i hovudboka", "correct errors in the general ledger", "correct ledger", "korrigere bilag", "Hauptbuchkorrektur", "Korrektur Hauptbuch", "corriger le grand livre", "corregir el libro mayor", "corrigir razão geral" — use task_type "correct_ledger". IMPORTANT: The prompt will always enumerate exactly 4 specific errors. You MUST extract all of them. Extract EACH individual error as a separate numbered entity: "correction1", "correction2", "correction3", "correction4", etc. Each correction entity must have:
          - "errorType": exactly one of "wrong_account" (booked on wrong account), "duplicate" (duplicate/double entry), "missing_vat" (VAT posting missing), "wrong_amount" (incorrect amount booked)
          - "account": the account number STRING (e.g. "6540") where the error appears
          - "correctAccount": ONLY for "wrong_account" — the correct account number string (e.g. "6860")
          - "amount": the amount involved (positive number, always excl. VAT)
          - "postedAmount": ONLY for "wrong_amount" — the incorrectly posted amount (number)
          - "correctAmount": ONLY for "wrong_amount" — the correct amount (number)
          - "vatAccount": ONLY for "missing_vat" — VAT account number, typically "2710"
        - For simplified annual accounts / year-end closing tasks (forenklet årsoppgjør, simplified annual accounts, cierre contable anual, clôture annuelle, Jahresabschluss, fechamento contábil) that involve depreciation (avskrivninger, depreciation), prepaid reversals (forskuddsbetalte kostnader, prepaid), and/or tax calculations, use task_type "annual_accounts". Extract as follows:
          - Create an "annualAccounts" entity with: "date" (YYYY-MM-DD, typically Dec 31 of the year), "depreciationExpenseAccount" (e.g. "6010"), "accumulatedDepreciationAccount" (e.g. "1209"), "prepaidAccount" (e.g. "1700"), "prepaidAmount" (number), "taxExpenseAccount" (e.g. "8700"), "taxPayableAccount" (e.g. "2920"), "taxRate" (e.g. 0.22).
          - For each fixed asset to depreciate, create a separate entity "asset1", "asset2", "asset3" etc. with: "name" (asset name), "bookValue" (current book value as number), "usefulLife" (remaining useful life in years as number), "assetAccount" (the asset's balance sheet account number, e.g. "1210").
          - IMPORTANT: Each asset gets its OWN entity key (asset1, asset2, asset3) — do NOT nest them under annualAccounts.
          - Example: asset1: {"name": "IT-utstyr", "bookValue": 382900, "usefulLife": 5, "assetAccount": "1210"}
          - For each provision (avsetning, provision, provisión, provision, Rückstellung) mentioned in the prompt, create a separate entity "provision1", "provision2", etc. with: "debitAccount" (the expense/cost account number string, e.g. "5000"), "creditAccount" (the liability/payable account number string, e.g. "2900"), "amount" (number, if stated in the prompt).
          - Example: provision1: {"debitAccount": "5000", "creditAccount": "2900", "amount": 15000}
        """;

    public LlmExtractor(string apiKey, ILogger<LlmExtractor> logger)
    {
        _logger = logger;
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://models.github.ai/inference")
        };
        var client = new OpenAIClient(credential, options);
        _chatClient = client.GetChatClient("openai/gpt-4o");
    }

    public async Task<ExtractionResult> ExtractAsync(string prompt, List<SolveFile>? files = null)
    {
        _logger.LogInformation("LLM extracting task from prompt ({Length} chars, {FileCount} files)",
            prompt.Length, files?.Count ?? 0);

        // Build user message parts
        var parts = new List<ChatMessageContentPart>();

        // Process files: extract PDF text, send images as vision
        var fileContext = ProcessFiles(files);
        if (!string.IsNullOrEmpty(fileContext.Text))
        {
            parts.Add(ChatMessageContentPart.CreateTextPart(
                $"Attached file content:\n{fileContext.Text}\n\n---\nTask prompt:"));
        }

        parts.Add(ChatMessageContentPart.CreateTextPart(prompt));

        // Add images for GPT-4o vision
        foreach (var img in fileContext.Images)
        {
            parts.Add(ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(img.Data), img.MimeType));
        }

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(parts)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0f,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        // Retry up to 3 times for content filter or transient errors, then fall back to regex
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var completion = await _chatClient.CompleteChatAsync(messages, options);
                var content = completion.Value.Content[0].Text;

                _logger.LogInformation("LLM response: {Content}", content);

                var result = SafeDeserialize(content);
                var final = result ?? new ExtractionResult { TaskType = "unknown" };
                final.RawPrompt = prompt;
                    NormalizeEmployeeAdminRole(final);
                NormalizeFileBasedVoucherAmounts(final, fileContext.Text);
                ValidateDates(final);
                ValidateExtraction(final);
                return final;
            }
            catch (Exception ex)
            {
                var isRetryable = ex.Message.Contains("content management policy") || ex.Message.Contains("content_filter") || ex.Message.Contains("429") || ex.Message.Contains("Incomplete employee extraction");
                if (attempt < 3 && isRetryable)
                {
                    _logger.LogWarning("LLM attempt {Attempt} failed ({Error}), retrying...", attempt, ex.Message.Split('\n')[0]);
                    await Task.Delay(attempt * 500);
                }
                else
                {
                    _logger.LogWarning("LLM extraction failed after {Attempt} attempts, using regex fallback: {Error}", attempt, ex.Message.Split('\n')[0]);
                    return RegexFallbackExtract(prompt);
                }
            }
        }

        // Should never reach here, but just in case
        return RegexFallbackExtract(prompt);
    }

    /// <summary>Deserialize LLM output, handling object-valued relationships by moving them to entities.</summary>
    private ExtractionResult? SafeDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtractionResult>(json);
        }
        catch (JsonException)
        {
            // Likely an object-valued relationship — fix it up
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new ExtractionResult
        {
            TaskType = root.TryGetProperty("task_type", out var tt) ? tt.GetString() ?? "unknown" : "unknown",
            Action = root.TryGetProperty("action", out var act) ? act.GetString() ?? "create" : "create",
            FilesNeeded = root.TryGetProperty("files_needed", out var fn) && fn.GetBoolean(),
            Language = root.TryGetProperty("language", out var lang) ? lang.GetString() : null
        };

        // Parse entities
        if (root.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Object)
        {
            foreach (var entity in entities.EnumerateObject())
            {
                if (entity.Value.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in entity.Value.EnumerateObject())
                        dict[prop.Name] = prop.Value.Clone();
                    result.Entities[entity.Name] = dict;
                }
            }
        }

        // Parse relationships — convert objects to entities, keep strings
        if (root.TryGetProperty("relationships", out var rels) && rels.ValueKind == JsonValueKind.Object)
        {
            foreach (var rel in rels.EnumerateObject())
            {
                if (rel.Value.ValueKind == JsonValueKind.String)
                {
                    result.Relationships[rel.Name] = rel.Value.GetString()!;
                }
                else if (rel.Value.ValueKind == JsonValueKind.Object)
                {
                    // Move object to entities if not already there
                    if (!result.Entities.ContainsKey(rel.Name))
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in rel.Value.EnumerateObject())
                            dict[prop.Name] = prop.Value.Clone();
                        result.Entities[rel.Name] = dict;
                    }
                    // Also set string relationship as the first name-like field
                    var nameStr = rel.Value.TryGetProperty("name", out var n) ? n.GetString()
                        : rel.Value.TryGetProperty("firstName", out var fn2) && rel.Value.TryGetProperty("lastName", out var ln)
                            ? $"{fn2.GetString()} {ln.GetString()}" : null;
                    if (nameStr != null)
                        result.Relationships[rel.Name] = nameStr;
                }
            }
        }

        // Parse raw_amounts
        if (root.TryGetProperty("raw_amounts", out var amounts) && amounts.ValueKind == JsonValueKind.Array)
            foreach (var a in amounts.EnumerateArray())
                result.RawAmounts.Add(a.GetString() ?? a.GetRawText());

        // Parse dates
        if (root.TryGetProperty("dates", out var dates) && dates.ValueKind == JsonValueKind.Array)
            foreach (var d in dates.EnumerateArray())
                result.Dates.Add(d.GetString() ?? d.GetRawText());

        return result;
    }

    private void NormalizeFileBasedVoucherAmounts(ExtractionResult extracted, string fileText)
    {
        if (extracted.TaskType != "create_voucher" || string.IsNullOrWhiteSpace(fileText))
            return;

        if (!extracted.Entities.TryGetValue("voucher", out var voucher))
            return;

        if (!voucher.ContainsKey("supplierName") && !voucher.ContainsKey("supplierOrgNumber"))
            return;

        var labeledTotal = TryExtractLabeledTotal(fileText);
        if (!labeledTotal.HasValue || labeledTotal.Value <= 0)
            return;

        var currentAmount = GetDecimalField(voucher, "amount")
            ?? GetDecimalField(voucher, "amountGross")
            ?? GetDecimalField(voucher, "totalAmount");

        if (currentAmount.HasValue && Math.Abs(currentAmount.Value - labeledTotal.Value) < 0.01m)
            return;

        voucher["amount"] = labeledTotal.Value.ToString("F2", CultureInfo.InvariantCulture);

        if (!extracted.RawAmounts.Any(a => decimal.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && Math.Abs(parsed - labeledTotal.Value) < 0.01m))
        {
            extracted.RawAmounts.Insert(0, labeledTotal.Value.ToString("F2", CultureInfo.InvariantCulture));
        }

        _logger.LogInformation("Normalized voucher amount from {Current} to labeled total {Total} based on attached file text",
            currentAmount?.ToString("F2", CultureInfo.InvariantCulture) ?? "<missing>",
            labeledTotal.Value.ToString("F2", CultureInfo.InvariantCulture));
    }

    private static decimal? TryExtractLabeledTotal(string text)
    {
        var patterns = new[]
        {
            @"(?:Totalt|Total|Totalsum|Sum|Summe|Gesamt|Monto total|Montant total|Valor total|Total a pagar)\s*:?\s*(\d[\d\s.,]*)\s*kr?",
            @"(?:Totalt|Total|Totalsum|Sum|Summe|Gesamt|Monto total|Montant total|Valor total|Total a pagar)(\d[\d\s.,]*)\s*kr?"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var amountText = match.Groups[1].Value
                .Replace(" ", string.Empty)
                .Replace(',', '.');

            if (decimal.TryParse(amountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                return amount;
        }

        return null;
    }

    private static decimal? GetDecimalField(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            float floatValue => Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture),
            int intValue => intValue,
            long longValue => longValue,
            JsonElement { ValueKind: JsonValueKind.Number } numberElement => numberElement.GetDecimal(),
            JsonElement { ValueKind: JsonValueKind.String } stringElement when decimal.TryParse(stringElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private void NormalizeEmployeeAdminRole(ExtractionResult result)
    {
        if (result.TaskType is not ("create_employee" or "update_employee"))
            return;

        var employee = result.Entities.GetValueOrDefault("employee");
        if (employee == null)
            return;

        var normalizedRoles = GetStringList(employee.GetValueOrDefault("roles"));
        if (normalizedRoles.Any(IsAdminPrompt))
        {
            employee["roles"] = new List<string> { "admin" };
            return;
        }

        if (IsAdminPrompt(result.RawPrompt))
            employee["roles"] = new List<string> { "admin" };
    }

    private static List<string> GetStringList(object? value)
    {
        if (value is null)
            return new List<string>();

        return value switch
        {
            string stringValue => new List<string> { stringValue },
            IEnumerable<string> strings => strings.ToList(),
            JsonElement { ValueKind: JsonValueKind.String } stringElement => new List<string> { stringElement.GetString() ?? string.Empty },
            JsonElement { ValueKind: JsonValueKind.Array } arrayElement => arrayElement.EnumerateArray()
                .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                .ToList(),
            IEnumerable<object> objects => objects.Select(static item => item?.ToString() ?? string.Empty).ToList(),
            _ => new List<string>()
        };
    }

    private static bool IsAdminPrompt(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, AdminPromptPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string AdminPromptPattern =
        @"\b(administrator|admin|account administrator|kontoadministrator|administratortilgang|administratortilgong|administrator access|admin access|grant administrator|grant admin|special privileges|elevated privileges|full privileges|all privileges|administrador|administrateur|administratorrettigheter|administrator-rettigheter|administrator role)\b";

    /// <summary>Regex-based fallback when LLM is unavailable (content filter, rate limit, etc.)</summary>
    private ExtractionResult RegexFallbackExtract(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var result = new ExtractionResult { Action = "create" };

        // Detect task type by keywords (multi-language)
        // Payroll/salary must come before employee — payroll prompts mention employee names but aren't employee-creation tasks
        if (Regex.IsMatch(lower, @"\b(gehaltsabrechnung|gehalt|lohnabrechnung|payroll|salary|lønn|lønnskjøring|lønnsslipp|nómina|salario|salário|folha de pagamento|paie|fiche de paie|run_payroll)\b"))
        {
            result.TaskType = "run_payroll";
            var emp = new Dictionary<string, object>();
            var pay = new Dictionary<string, object>();

            // Extract email
            var emailMatch = Regex.Match(prompt, @"[\w.-]+@[\w.-]+\.\w{2,}");
            if (emailMatch.Success) emp["email"] = emailMatch.Value;

            // Extract name — look for "Name (email)" or "für/for/de Name" patterns
            var nameMatch = Regex.Match(prompt, @"(?:für|for|de|para|pour|av)\s+([A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+(?:\s+[A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+)+)", RegexOptions.None);
            if (nameMatch.Success)
            {
                var parts = nameMatch.Groups[1].Value.Trim().Split(' ', 2);
                emp["firstName"] = parts[0];
                emp["lastName"] = parts.Length > 1 ? parts[1] : parts[0];
            }

            // Extract amounts — first number = baseSalary, second = bonus
            var amounts = Regex.Matches(prompt, @"(\d[\d\s]*)\s*NOK");
            if (amounts.Count > 0 && decimal.TryParse(amounts[0].Groups[1].Value.Replace(" ", ""), out var base1))
                pay["baseSalary"] = base1;
            if (amounts.Count > 1 && decimal.TryParse(amounts[1].Groups[1].Value.Replace(" ", ""), out var bonus1))
                pay["bonus"] = bonus1;

            result.Entities["employee"] = emp;
            result.Entities["payroll"] = pay;
        }
        else if (Regex.IsMatch(lower, @"\b(ansatt|employee|empleado|empregado|mitarbeiter|employé|tilsett)\b"))
        {
            result.TaskType = lower.Contains("oppdater") || lower.Contains("update") || lower.Contains("endre") ? "update_employee" : "create_employee";

            var emp = new Dictionary<string, object>();

            // Extract name in quotes or after "navn/name/nombre/nom"
            var nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome|namens|llamad[oa]|nommée?|chamad[oa]|heiter|heter)\s+'?([A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+(?:\s+[A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+)+)", RegexOptions.None);
            if (!nameMatch.Success)
                nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome|llamad[oa]|nommée?|heiter|heter)\s*'([^']+)'", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                var parts = nameMatch.Groups[1].Value.Trim().Split(' ', 2);
                emp["firstName"] = parts[0];
                emp["lastName"] = parts.Length > 1 ? parts[1] : parts[0];
            }
            // Also try "fornavn" / "etternavn" pattern
            var fnMatch = Regex.Match(prompt, @"(?:fornavn|first\s*name)\s*'([^']+)'", RegexOptions.IgnoreCase);
            var lnMatch = Regex.Match(prompt, @"(?:etternavn|last\s*name|surname)\s*'([^']+)'", RegexOptions.IgnoreCase);
            if (fnMatch.Success) emp["firstName"] = fnMatch.Groups[1].Value;
            if (lnMatch.Success) emp["lastName"] = lnMatch.Groups[1].Value;

            // Extract email
            var emailMatch = Regex.Match(prompt, @"[\w.-]+@[\w.-]+\.\w{2,}");
            if (emailMatch.Success) emp["email"] = emailMatch.Value;

            // Extract phone
            var phoneMatch = Regex.Match(prompt, @"(?:telefon|phone|mobil|tlf)[^\d]*([+\d\s]{8,})", RegexOptions.IgnoreCase);
            if (phoneMatch.Success) emp["phoneNumberMobile"] = phoneMatch.Groups[1].Value.Trim();

            // Extract dates (dateOfBirth, startDate) — look for date patterns near keywords
            var dobMatch = Regex.Match(prompt, @"(?:født|fødd|born|geboren|nascid[oa]|nacid[oa]|né[e]?|fødselsdato|date\s*of\s*birth)\s*(?:el\s*|em\s*|am\s*|den\s*|le\s*|:?\s*)(\d{1,2})[.\s]+(\w+)\s+(\d{4})", RegexOptions.IgnoreCase);
            if (dobMatch.Success) { var d = TryParseDate(dobMatch); if (d != null) emp["dateOfBirth"] = d; }

            var sdMatch = Regex.Match(prompt, @"(?:startdato|startdatum|start\s*date|fecha\s*de\s*inicio|data\s*de\s*início|date\s*de\s*début|début)\s*(?:el\s*|am\s*|den\s*|le\s*|:?\s*)(\d{1,2})[.\s]+(\w+)\s+(\d{4})", RegexOptions.IgnoreCase);
            if (sdMatch.Success) { var d = TryParseDate(sdMatch); if (d != null) emp["startDate"] = d; }

            // Check for admin role
                if (IsAdminPrompt(lower))
                emp["roles"] = new List<string> { "admin" };

            result.Entities["employee"] = emp;
        }
        else if (Regex.IsMatch(lower, @"\b(kunde|customer|cliente|client|Kunde)\b"))
        {
            result.TaskType = "create_customer";
            var cust = new Dictionary<string, object>();
            var nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome)\s*'([^']+)'", RegexOptions.IgnoreCase);
            if (nameMatch.Success) cust["name"] = nameMatch.Groups[1].Value;
            var emailMatch = Regex.Match(prompt, @"[\w.-]+@[\w.-]+\.\w{2,}");
            if (emailMatch.Success) cust["email"] = emailMatch.Value;
            var orgMatch = Regex.Match(prompt, @"(?:organisasjonsnummer|org\.?\s*nr|org\.?\s*number|CIF|CNPJ)\s*'?(\d{9})'?", RegexOptions.IgnoreCase);
            if (orgMatch.Success) cust["organizationNumber"] = orgMatch.Groups[1].Value;
            result.Entities["customer"] = cust;
        }
        else if (Regex.IsMatch(lower, @"\b(produkt|product|producto|produit|Produkt)\b"))
        {
            result.TaskType = "create_product";
            var prod = new Dictionary<string, object>();
            var nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome)\s*'([^']+)'", RegexOptions.IgnoreCase);
            if (nameMatch.Success) prod["name"] = nameMatch.Groups[1].Value;
            result.Entities["product"] = prod;
        }
        else if (Regex.IsMatch(lower, @"\b(avdeling|department|departamento|département|Abteilung)\b"))
        {
            result.TaskType = "create_department";
            var dept = new Dictionary<string, object>();
            var nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome)\s*'([^']+)'", RegexOptions.IgnoreCase);
            if (nameMatch.Success) dept["name"] = nameMatch.Groups[1].Value;
            result.Entities["department"] = dept;
        }
        else if (Regex.IsMatch(lower, @"\b(leverandør|supplier|proveedor|fournisseur|Lieferant|fornecedor)\b"))
        {
            result.TaskType = "create_supplier";
            var sup = new Dictionary<string, object>();
            var nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome)\s*'([^']+)'", RegexOptions.IgnoreCase);
            if (nameMatch.Success) sup["name"] = nameMatch.Groups[1].Value;
            result.Entities["supplier"] = sup;
        }
        else if (Regex.IsMatch(lower, @"\b(betaling|payment|pago|pagamento|Zahlung|paiement|innbetaling)\b"))
        {
            result.TaskType = "register_payment";
        }
        else if (Regex.IsMatch(lower, @"\b(kreditnota|credit\s*note|nota de crédito|Gutschrift|note de crédit)\b"))
        {
            result.TaskType = "create_credit_note";
        }
        else if (Regex.IsMatch(lower, @"\b(faktura|invoice|factura|fatura|Rechnung|facture)\b"))
        {
            result.TaskType = "create_invoice";
        }
        else if (Regex.IsMatch(lower, @"\b(prosjekt|project|proyecto|projet|Projekt|projeto)\b"))
        {
            result.TaskType = "create_project";
            var proj = new Dictionary<string, object>();
            var nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome)\s*'([^']+)'", RegexOptions.IgnoreCase);
            if (nameMatch.Success) proj["name"] = nameMatch.Groups[1].Value;
            result.Entities["project"] = proj;
        }
        else if (Regex.IsMatch(lower, @"\b(reise|travel|viaje|viagem|Reise|voyage)\b"))
        {
            result.TaskType = lower.Contains("slett") || lower.Contains("delete") || lower.Contains("eliminar") ? "delete_travel_expense" : "create_travel_expense";
            if (result.TaskType == "delete_travel_expense") result.Action = "delete";
        }
        else if (Regex.IsMatch(lower, @"\b(bilag|voucher|journal|asiento|lançamento|Buchung|écriture)\b"))
        {
            result.TaskType = "create_voucher";
        }
        else if (Regex.IsMatch(lower, @"\b(slett|delete|eliminar|excluir|löschen|supprimer)\b"))
        {
            result.TaskType = "delete_entity";
            result.Action = "delete";
        }
        else if (Regex.IsMatch(lower, @"\b(modul|module|módulo)\b"))
        {
            result.TaskType = "enable_module";
        }
        else if (Regex.IsMatch(lower, @"\b(bankavstemming|bankavstемming|bank\s*reconcili|reconciliar\s*cuenta|rapprochement\s*bancaire|kontenabstimmung|bankutskrift|bankbalanse)\b"))
        {
            result.TaskType = "bank_reconciliation";
            var rec = new Dictionary<string, object>();
            var balMatch = Regex.Match(prompt, @"(\d[\d\s]*[.,]\d{2})\b");
            if (balMatch.Success && decimal.TryParse(balMatch.Groups[1].Value.Replace(" ", "").Replace(",", "."), out var bal))
                rec["closingBalance"] = bal;
            rec["accountNumber"] = "1920";
            result.Entities["reconciliation"] = rec;
        }
        else if (Regex.IsMatch(lower, @"\b(timeregistrering|logge?\s*timer?|timer?\s*p[åa]\s*prosjekt|registrer\s*timer?|log\s*hours?|create_timesheet|stunden\s*erfassen|enregistrer\s*heures?|registrar\s*horas?)\b"))
        {
            result.TaskType = "create_timesheet";
            var ts = new Dictionary<string, object>();
            var hoursMatch = Regex.Match(prompt, @"(\d+(?:[.,]\d+)?)\s*(?:timer?|hours?|horas?|heures?|Stunden)", RegexOptions.IgnoreCase);
            if (hoursMatch.Success && decimal.TryParse(hoursMatch.Groups[1].Value.Replace(",", "."), out var hours))
                ts["hours"] = hours;
            result.Entities["timesheet"] = ts;
        }
        else if (Regex.IsMatch(lower, @"\b(kontaktperson|contact\s*person|persona\s*de\s*contacto|personne\s*de\s*contact|ansprechpartner)\b"))
        {
            result.TaskType = "create_contact";
            var contact = new Dictionary<string, object>();
            var emailMatch2 = Regex.Match(prompt, @"[\w.-]+@[\w.-]+\.\w{2,}");
            if (emailMatch2.Success) contact["email"] = emailMatch2.Value;
            result.Entities["contact"] = contact;
        }
        else if (Regex.IsMatch(lower, @"\b(årsoppgjør|avskrivning|annual\s*accounts|depreciation|amortissement|jahresabschluss|depreciaci[oó]n|ammortamento|forskuddsbetalt|prepaid|skattekostnad)\b"))
        {
            result.TaskType = "annual_accounts";
        }
        else if (Regex.IsMatch(lower, @"(rette?\s*feil|korriger|ledger\s*correct|correct\s*ledger|correct\s*error|hauptbuchkorrektur|feil\s*i\s*(hoved|hovud)boka?|errors?\s+in\s+.*ledger|corriger.*grand\s*livre|corregir.*libro)"))
        {
            result.TaskType = "correct_ledger";
        }
        else
        {
            result.TaskType = "unknown";
        }

        // Extract amounts
        var amountMatches = Regex.Matches(prompt, @"(\d[\d\s]*[.,]\d{2})\b");
        foreach (Match m in amountMatches)
            result.RawAmounts.Add(m.Groups[1].Value.Replace(" ", "").Replace(",", "."));

        // Extract dates
        var dateMatches = Regex.Matches(prompt, @"(\d{4}-\d{2}-\d{2}|\d{1,2}[./]\d{1,2}[./]\d{4})");
        foreach (Match m in dateMatches)
            result.Dates.Add(m.Groups[1].Value);

        _logger.LogInformation("Regex fallback extracted: task_type={TaskType}, entities={Entities}",
            result.TaskType, System.Text.Json.JsonSerializer.Serialize(result.Entities));

        result.RawPrompt = prompt;
        return result;
    }

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1,
        ["february"] = 2,
        ["march"] = 3,
        ["april"] = 4,
        ["may"] = 5,
        ["june"] = 6,
        ["july"] = 7,
        ["august"] = 8,
        ["september"] = 9,
        ["october"] = 10,
        ["november"] = 11,
        ["december"] = 12,
        // Norwegian/German shared
        ["januar"] = 1,
        ["februar"] = 2,
        ["mars"] = 3,
        ["mai"] = 5,
        ["juni"] = 6,
        ["juli"] = 7,
        ["oktober"] = 10,
        ["desember"] = 12,
        // German unique
        ["märz"] = 3,
        ["dezember"] = 12,
        // Spanish
        ["enero"] = 1,
        ["febrero"] = 2,
        ["marzo"] = 3,
        ["abril"] = 4,
        ["mayo"] = 5,
        ["junio"] = 6,
        ["julio"] = 7,
        ["agosto"] = 8,
        ["septiembre"] = 9,
        ["octubre"] = 10,
        ["noviembre"] = 11,
        ["diciembre"] = 12,
        // Portuguese
        ["janeiro"] = 1,
        ["fevereiro"] = 2,
        ["março"] = 3,
        ["maio"] = 5,
        ["junho"] = 6,
        ["julho"] = 7,
        ["setembro"] = 9,
        ["outubro"] = 10,
        ["dezembro"] = 12,
        // French
        ["janvier"] = 1,
        ["février"] = 2,
        ["avril"] = 4,
        ["juin"] = 6,
        ["juillet"] = 7,
        ["août"] = 8,
        ["septembre"] = 9,
        ["octobre"] = 10,
        ["décembre"] = 12,
    };

    public static string? TryParseDate(Match m)
    {
        if (int.TryParse(m.Groups[1].Value, out var day) && int.TryParse(m.Groups[3].Value, out var year))
        {
            var monthStr = m.Groups[2].Value.TrimEnd('.').ToLowerInvariant();
            if (MonthNames.TryGetValue(monthStr, out var month))
                return $"{year:D4}-{month:D2}-{day:D2}";
            if (int.TryParse(monthStr, out month) && month >= 1 && month <= 12)
                return $"{year:D4}-{month:D2}-{day:D2}";
        }
        return null;
    }

    /// <summary>Validate and fix dates in extraction result. Snaps invalid dates (e.g. Feb 29 on non-leap year) to last valid day of month.</summary>
    private void ValidateExtraction(ExtractionResult result)
    {
        // Detect incomplete employee extraction (LLM sometimes omits name fields) and force retry
        if (result.TaskType == "create_employee")
        {
            var emp = result.Entities.GetValueOrDefault("employee");
            if (emp != null
                && !emp.ContainsKey("firstName")
                && !emp.ContainsKey("lastName")
                && !emp.ContainsKey("name"))
            {
                _logger.LogWarning("Incomplete employee extraction — missing firstName, lastName, and name. Forcing retry.");
                throw new InvalidOperationException("Incomplete employee extraction: missing name fields");
            }
        }
    }

    private void ValidateDates(ExtractionResult result)
    {
        // Fix dates in the dates list
        for (int i = 0; i < result.Dates.Count; i++)
        {
            var fixedDate = FixDateIfInvalid(result.Dates[i]);
            if (fixedDate != null && fixedDate != result.Dates[i])
            {
                _logger.LogWarning("Fixed invalid date {Original} → {Fixed}", result.Dates[i], fixedDate);
                result.Dates[i] = fixedDate;
            }
        }

        // Fix date fields in entities
        foreach (var entity in result.Entities.Values)
        {
            foreach (var key in entity.Keys.ToList())
            {
                if (!key.Contains("date", StringComparison.OrdinalIgnoreCase) &&
                    !key.Contains("Date", StringComparison.Ordinal)) continue;

                var val = entity[key];
                var dateStr = val is JsonElement je ? je.GetString() : val?.ToString();
                if (string.IsNullOrEmpty(dateStr)) continue;

                var fixedDate = FixDateIfInvalid(dateStr);
                if (fixedDate != null && fixedDate != dateStr)
                {
                    _logger.LogWarning("Fixed invalid entity date {Key}: {Original} → {Fixed}", key, dateStr, fixedDate);
                    entity[key] = fixedDate;
                }
            }
        }
    }

    private static string? FixDateIfInvalid(string dateStr)
    {
        if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return dateStr; // Already valid

        // Try to extract year-month-day and snap to last valid day
        var match = Regex.Match(dateStr, @"^(\d{4})-(\d{2})-(\d{2})$");
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var year) &&
            int.TryParse(match.Groups[2].Value, out var month) &&
            month >= 1 && month <= 12 &&
            year >= 1900 && year <= 2100)
        {
            var maxDay = DateTime.DaysInMonth(year, month);
            return $"{year:D4}-{month:D2}-{maxDay:D2}";
        }

        return null; // Can't fix, leave as-is
    }

    private FileProcessingResult ProcessFiles(List<SolveFile>? files)
    {
        var result = new FileProcessingResult();
        if (files == null || files.Count == 0) return result;

        foreach (var file in files)
        {
            try
            {
                var data = Convert.FromBase64String(file.ContentBase64);

                if (file.MimeType == "application/pdf")
                {
                    using var doc = PdfDocument.Open(data);
                    var text = string.Join("\n", doc.GetPages().Select(p => p.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Text += $"[File: {file.Filename}]\n{text}\n\n";
                        _logger.LogInformation("Extracted {Chars} chars from PDF {File}",
                            text.Length, file.Filename);
                    }
                    else
                    {
                        // PDF has no extractable text — treat as image (scanned PDF)
                        result.Images.Add(new ImageData(data, "application/pdf"));
                        _logger.LogInformation("PDF {File} has no text, sending as image", file.Filename);
                    }
                }
                else if (file.MimeType.StartsWith("image/"))
                {
                    result.Images.Add(new ImageData(data, file.MimeType));
                    _logger.LogInformation("Added image {File} ({MimeType}, {Size} bytes)",
                        file.Filename, file.MimeType, data.Length);
                }
                else
                {
                    // Unknown file type — try to read as text
                    var text = System.Text.Encoding.UTF8.GetString(data);
                    result.Text += $"[File: {file.Filename}]\n{text}\n\n";
                    _logger.LogInformation("Read {File} as text ({Chars} chars)", file.Filename, text.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process file {File}", file.Filename);
            }
        }

        return result;
    }

    private class FileProcessingResult
    {
        public string Text { get; set; } = "";
        public List<ImageData> Images { get; } = new();
    }

    private record ImageData(byte[] Data, string MimeType);
}
