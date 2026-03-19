using System.ClientModel;
using System.Text.Json;
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
        You are an accounting task parser for the Tripletex API. Given a task
        prompt (in any of 7 languages), extract structured data for execution.

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
          "files_needed": true | false
        }

        Rules:
        - Copy field values VERBATIM from the prompt (names, emails, org numbers)
        - Parse monetary amounts as numbers (strip currency symbols)
        - Convert all dates to YYYY-MM-DD format
        - If the task type is ambiguous, use "unknown"
        - Norwegian "kontoadministrator" = role "administrator"
        - Spanish "administrador" = role "administrator"
        - German "Kontoverwalter" or "Administrator" = role "administrator"
        - French "administrateur" = role "administrator"
        - Portuguese "administrador" = role "administrator"
        - English "account administrator" or "admin" = role "administrator"
        - Nynorsk "kontoadministrator" = role "administrator"
        - For employee tasks, extract "roles" as an array (e.g. ["administrator"])
        - For invoice tasks, extract customer info, order lines with description/count/unitPrice, and invoice dates
        - For travel expense, extract employee reference, title, travel details, and cost items
        - If the prompt mentions registering/recording a payment ("betaling", "payment", "pago", "pagamento", "Zahlung", "paiement", "innbetaling"), use "register_payment" even if it also describes creating the invoice
        - For credit notes ("kreditnota", "credit note", "nota de crédito", "Gutschrift", "note de crédit"), use "create_credit_note"
        - For vouchers/journal entries ("bilag", "voucher", "journal entry", "asiento", "lançamento", "Buchung", "écriture"), use "create_voucher"
        - For deleting entities, use "delete_entity" and set action to "delete"
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

        var completion = await _chatClient.CompleteChatAsync(messages, options);
        var content = completion.Value.Content[0].Text;

        _logger.LogInformation("LLM response: {Content}", content);

        var result = JsonSerializer.Deserialize<ExtractionResult>(content);
        return result ?? new ExtractionResult { TaskType = "unknown" };
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
