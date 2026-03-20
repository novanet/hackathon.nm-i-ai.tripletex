using System.ClientModel;
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
            "create_invoice", "register_payment", "create_credit_note",
            "create_travel_expense", "delete_travel_expense",
            "create_project", "create_supplier", "create_voucher",
            "delete_entity", "enable_module", "unknown"],
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
        - Parse monetary amounts as numbers (strip currency symbols)
        - Convert all dates to YYYY-MM-DD format
        - If the task type is ambiguous, use "unknown"
        - For employee tasks, always split full name into "firstName" and "lastName". Include ALL mentioned fields: email, dateOfBirth (YYYY-MM-DD), startDate (YYYY-MM-DD), phoneNumberMobile, nationalIdentityNumber, bankAccountNumber
        - If the prompt grants special access or elevated role to an employee, set "roles": ["admin"]
        - For invoice tasks, extract customer info, order lines with description/count/unitPrice, and invoice dates
        - For travel expense, extract employee reference, title, travel details, and cost items
        - If the prompt mentions registering/recording a payment, use "register_payment" even if it also describes creating the invoice
        - For credit notes, use "create_credit_note"
        - For vouchers/journal entries/postings, use "create_voucher"
        - For deleting entities, use "delete_entity" and set action to "delete"
        - When creating MULTIPLE entities of the same type (e.g. "create 3 departments"), use separate entity keys: "department1": {"name": "A"}, "department2": {"name": "B"}, etc. Each entity gets its own key with a numeric suffix.
        - For projects referencing a customer like "Fjordkraft AS (org.nr 944845712)", put the CUSTOMER NAME in relationships.customer ("Fjordkraft AS") and the org number in the project entity as "customerOrgNumber": "944845712"
        - For projects, extract the project manager as a nested object: "projectManager": {"firstName": "...", "lastName": "...", "email": "..."}
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

                var result = JsonSerializer.Deserialize<ExtractionResult>(content);
                return result ?? new ExtractionResult { TaskType = "unknown" };
            }
            catch (Exception ex)
            {
                var isRetryable = ex.Message.Contains("content management policy") || ex.Message.Contains("content_filter") || ex.Message.Contains("429");
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

    /// <summary>Regex-based fallback when LLM is unavailable (content filter, rate limit, etc.)</summary>
    private ExtractionResult RegexFallbackExtract(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var result = new ExtractionResult { Action = "create" };

        // Detect task type by keywords (multi-language)
        if (Regex.IsMatch(lower, @"\b(ansatt|employee|empleado|empregado|mitarbeiter|employé|tilsett)\b"))
        {
            result.TaskType = lower.Contains("oppdater") || lower.Contains("update") || lower.Contains("endre") ? "update_employee" : "create_employee";

            var emp = new Dictionary<string, object>();

            // Extract name in quotes or after "navn/name/nombre/nom"
            var nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome|namens)\s+'?([A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+(?:\s+[A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+)+)", RegexOptions.None);
            if (!nameMatch.Success)
                nameMatch = Regex.Match(prompt, @"(?:navn|name|nombre|nom|Nome)\s*'([^']+)'", RegexOptions.IgnoreCase);
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
            var dobMatch = Regex.Match(prompt, @"(?:født|born|geboren|nascid[oa]|né[e]?|fødselsdato|date\s*of\s*birth)\s*(?:am\s*|den\s*|:?\s*)(\d{1,2})[.\s]+(\w+)\s+(\d{4})", RegexOptions.IgnoreCase);
            if (dobMatch.Success) { var d = ParseDate(dobMatch); if (d != null) emp["dateOfBirth"] = d; }

            var sdMatch = Regex.Match(prompt, @"(?:startdato|startdatum|start\s*date|fecha\s*de\s*inicio|data\s*de\s*início|date\s*de\s*début)\s*(?::?\s*)(\d{1,2})[.\s]+(\w+)\s+(\d{4})", RegexOptions.IgnoreCase);
            if (sdMatch.Success) { var d = ParseDate(sdMatch); if (d != null) emp["startDate"] = d; }

            // Check for admin role
            if (Regex.IsMatch(lower, @"administrator|admin|kontoadministrator|administratortilgang|elevated|privileges"))
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

    private static string? ParseDate(Match m)
    {
        if (int.TryParse(m.Groups[1].Value, out var day) && int.TryParse(m.Groups[3].Value, out var year))
        {
            var monthStr = m.Groups[2].Value.TrimEnd('.');
            if (MonthNames.TryGetValue(monthStr, out var month))
                return $"{year:D4}-{month:D2}-{day:D2}";
            if (int.TryParse(monthStr, out month) && month >= 1 && month <= 12)
                return $"{year:D4}-{month:D2}-{day:D2}";
        }
        return null;
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
