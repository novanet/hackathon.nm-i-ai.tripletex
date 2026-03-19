using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class FallbackAgentHandler : ITaskHandler
{
    private readonly ILogger<FallbackAgentHandler> _logger;
    private readonly ChatClient _chatClient;
    private const int MaxIterations = 8;

    private static readonly ChatTool GetTool = ChatTool.CreateFunctionTool(
        "api_get",
        "Make a GET request to the Tripletex API. Returns the JSON response.",
        BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "API path, e.g. /customer?name=Acme&count=1" }
            },
            "required": ["path"]
        }
        """));

    private static readonly ChatTool PostTool = ChatTool.CreateFunctionTool(
        "api_post",
        "Make a POST request to the Tripletex API. Returns the JSON response.",
        BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "API path, e.g. /customer" },
                "body": { "type": "object", "description": "Request body as JSON object" }
            },
            "required": ["path", "body"]
        }
        """));

    private static readonly ChatTool PutTool = ChatTool.CreateFunctionTool(
        "api_put",
        "Make a PUT request to the Tripletex API. Returns the JSON response. Use query params in the path for action endpoints.",
        BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "API path with query params, e.g. /invoice/123/:payment?paymentDate=2026-01-01&paymentTypeId=1&paidAmount=500" },
                "body": { "type": "object", "description": "Request body as JSON object (optional for action endpoints)" }
            },
            "required": ["path"]
        }
        """));

    private static readonly ChatTool DeleteTool = ChatTool.CreateFunctionTool(
        "api_delete",
        "Make a DELETE request to the Tripletex API.",
        BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "API path, e.g. /customer/123" }
            },
            "required": ["path"]
        }
        """));

    private static readonly ChatTool DoneTool = ChatTool.CreateFunctionTool(
        "task_complete",
        "Call this when the task has been completed successfully.",
        BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "summary": { "type": "string", "description": "Brief summary of what was done" }
            },
            "required": ["summary"]
        }
        """));

    private const string AgentSystemPrompt = """
        You are a Tripletex API agent. You receive an accounting task and must execute it using the Tripletex API.

        CRITICAL RULES:
        - Use the provided tools to make API calls (api_get, api_post, api_put, api_delete)
        - When done, call task_complete
        - Response envelope: single entity = response.value, list = response.values
        - PUT updates MUST include the version field from prior GET/POST
        - Dates are YYYY-MM-DD format
        - For pagination use ?from=0&count=100
        - Action endpoints use : prefix (e.g. PUT /invoice/{id}/:payment)
        - Payment params go in query string, not body
        - NEVER guess IDs — always look them up via GET first
        - Copy field values VERBATIM from the prompt
        - Minimize API calls — every call counts for efficiency scoring
        - Every 4xx error permanently reduces the score — validate before sending

        COMMON PATTERNS:
        - Create customer: POST /customer {name, email, isCustomer: true, ...}
        - Create employee: POST /employee {firstName, lastName, ...}
        - Create product: POST /product {name, priceExcludingVatCurrency, vatType: {id: N}}
        - Create department: POST /department {name, departmentNumber}
        - Create supplier: POST /supplier {name, email, ...}
        - Create invoice: POST /customer → POST /order (with orderLines) → POST /invoice {orders: [{id}]}
        - Register payment: PUT /invoice/{id}/:payment?paymentDate=X&paymentTypeId=Y&paidAmount=Z
        - Create voucher: POST /ledger/voucher {date, description, postings: [{account: {id}, amountGross, amountGrossCurrency, row, date}]}
        - Lookup accounts: GET /ledger/account?number=XXXX&count=1&fields=id,number
        - Lookup VAT types: GET /ledger/vatType?count=100&fields=id,name,number,percentage
        - Delete entity: GET /entity?search → DELETE /entity/{id}
        """;

    public FallbackAgentHandler(string apiKey, ILogger<FallbackAgentHandler> logger)
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

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        await HandleWithPromptAsync(api, extracted, "");
    }

    public async Task HandleWithPromptAsync(TripletexApiClient api, ExtractionResult extracted, string originalPrompt, List<SolveFile>? files = null)
    {
        _logger.LogInformation("Fallback agent handling task type: {TaskType}", extracted.TaskType);

        var textContent = string.IsNullOrEmpty(originalPrompt)
            ? $"Execute this task:\n\n{JsonSerializer.Serialize(extracted)}"
            : $"Original prompt:\n{originalPrompt}\n\nExtracted data:\n{JsonSerializer.Serialize(extracted)}";

        // Build user message parts (text + optional file context)
        var parts = new List<ChatMessageContentPart>();

        // Add file context if present
        if (files != null && files.Count > 0)
        {
            foreach (var file in files)
            {
                try
                {
                    var data = Convert.FromBase64String(file.ContentBase64);
                    if (file.MimeType.StartsWith("image/"))
                    {
                        parts.Add(ChatMessageContentPart.CreateImagePart(
                            BinaryData.FromBytes(data), file.MimeType));
                    }
                    else if (file.MimeType == "application/pdf")
                    {
                        using var doc = UglyToad.PdfPig.PdfDocument.Open(data);
                        var pdfText = string.Join("\n", doc.GetPages().Select(p => p.Text));
                        if (!string.IsNullOrWhiteSpace(pdfText))
                            textContent = $"[Attached PDF: {file.Filename}]\n{pdfText}\n\n{textContent}";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallback agent: failed to process file {File}", file.Filename);
                }
            }
        }

        parts.Insert(0, ChatMessageContentPart.CreateTextPart(textContent));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(AgentSystemPrompt),
            new UserChatMessage(parts)
        };

        var chatOptions = new ChatCompletionOptions
        {
            Temperature = 0f,
            Tools = { GetTool, PostTool, PutTool, DeleteTool, DoneTool }
        };

        for (int i = 0; i < MaxIterations; i++)
        {
            var completion = await _chatClient.CompleteChatAsync(messages, chatOptions);
            var response = completion.Value;

            if (response.FinishReason == ChatFinishReason.Stop)
            {
                _logger.LogInformation("Fallback agent finished (model stopped)");
                return;
            }

            if (response.FinishReason != ChatFinishReason.ToolCalls)
            {
                _logger.LogWarning("Fallback agent unexpected finish reason: {Reason}", response.FinishReason);
                return;
            }

            // Add assistant message with tool calls
            messages.Add(new AssistantChatMessage(response));

            // Process each tool call
            foreach (var toolCall in response.ToolCalls)
            {
                var args = JsonDocument.Parse(toolCall.FunctionArguments).RootElement;
                string toolResult;

                try
                {
                    toolResult = toolCall.FunctionName switch
                    {
                        "api_get" => await HandleGet(api, args),
                        "api_post" => await HandlePost(api, args),
                        "api_put" => await HandlePut(api, args),
                        "api_delete" => await HandleDelete(api, args),
                        "task_complete" => HandleComplete(args),
                        _ => $"Unknown tool: {toolCall.FunctionName}"
                    };
                }
                catch (TripletexApiException ex)
                {
                    _logger.LogWarning("Fallback agent tool {Tool} error: {Error}", toolCall.FunctionName, ex.Message);
                    toolResult = $"API Error: {ex.Message}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fallback agent tool {Tool} exception", toolCall.FunctionName);
                    toolResult = $"Error: {ex.Message}";
                }

                messages.Add(new ToolChatMessage(toolCall.Id, toolResult));

                if (toolCall.FunctionName == "task_complete")
                {
                    _logger.LogInformation("Fallback agent completed task");
                    return;
                }
            }
        }

        _logger.LogWarning("Fallback agent reached max iterations ({Max})", MaxIterations);
    }

    private async Task<string> HandleGet(TripletexApiClient api, JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        _logger.LogInformation("Fallback agent GET {Path}", path);
        var result = await api.GetAsync(path);
        return TruncateResult(result);
    }

    private async Task<string> HandlePost(TripletexApiClient api, JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        var body = args.GetProperty("body");
        _logger.LogInformation("Fallback agent POST {Path}", path);
        var result = await api.PostAsync(path, body);
        return TruncateResult(result);
    }

    private async Task<string> HandlePut(TripletexApiClient api, JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        object? body = args.TryGetProperty("body", out var b) ? b : null;
        _logger.LogInformation("Fallback agent PUT {Path}", path);
        var result = await api.PutAsync(path, body);
        return TruncateResult(result);
    }

    private async Task<string> HandleDelete(TripletexApiClient api, JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        _logger.LogInformation("Fallback agent DELETE {Path}", path);
        await api.DeleteAsync(path);
        return "Deleted successfully";
    }

    private string HandleComplete(JsonElement args)
    {
        var summary = args.TryGetProperty("summary", out var s) ? s.GetString() : "done";
        _logger.LogInformation("Fallback agent summary: {Summary}", summary);
        return "Task marked complete";
    }

    private static string TruncateResult(JsonElement result)
    {
        var json = result.GetRawText();
        // Truncate large responses to avoid token overflow
        return json.Length > 4000 ? json[..4000] + "... (truncated)" : json;
    }
}
