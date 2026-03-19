using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using TripletexAgent.Models;

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

    public async Task<ExtractionResult> ExtractAsync(string prompt)
    {
        _logger.LogInformation("LLM extracting task from prompt ({Length} chars)", prompt.Length);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(prompt)
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
}
