using System.Diagnostics;
using System.Text.Json;
using Serilog;
using TripletexAgent.Handlers;
using TripletexAgent.Models;
using TripletexAgent.Services;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File($"logs/agent-{DateTime.Now:yyyyMMdd-HHmmss}.log")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// Register handlers
builder.Services.AddSingleton<EmployeeHandler>();
builder.Services.AddSingleton<CustomerHandler>();
builder.Services.AddSingleton<ProductHandler>();
builder.Services.AddSingleton<DepartmentHandler>();
builder.Services.AddSingleton<SupplierHandler>();
builder.Services.AddSingleton<InvoiceHandler>();
builder.Services.AddSingleton<PaymentHandler>();
builder.Services.AddSingleton<ProjectHandler>();
builder.Services.AddSingleton<TravelExpenseHandler>();
builder.Services.AddSingleton<CreditNoteHandler>();
builder.Services.AddSingleton<VoucherHandler>();
builder.Services.AddSingleton<DeleteEntityHandler>();
builder.Services.AddSingleton<EnableModuleHandler>();
builder.Services.AddSingleton<TaskRouter>();

// LLM extractor
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
    ?? builder.Configuration["GitHubToken"]
    ?? throw new InvalidOperationException("GITHUB_TOKEN environment variable (or GitHubToken in appsettings) is required");
builder.Services.AddSingleton(sp => new LlmExtractor(githubToken, sp.GetRequiredService<ILogger<LlmExtractor>>()));

var app = builder.Build();

// API key auth middleware — protects /solve from unauthorized access
var apiKey = app.Configuration["ApiKey"] ?? Environment.GetEnvironmentVariable("API_KEY");
if (!string.IsNullOrEmpty(apiKey))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/solve"))
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(authHeader) || authHeader != $"Bearer {apiKey}")
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
        await next();
    });
}

app.MapGet("/", () => "Tripletex Agent is running");

app.MapPost("/solve", async (HttpContext httpContext, SolveRequest request, LlmExtractor llm, TaskRouter router, ILogger<Program> logger) =>
{
    var sw = Stopwatch.StartNew();
    logger.LogInformation("Received /solve request ({PromptLength} chars, {FileCount} files)",
        request.Prompt.Length, request.Files?.Count ?? 0);

    ExtractionResult? extracted = null;
    TripletexApiClient? api = null;

    try
    {
        // Step 1: Extract structured data from prompt via LLM (with file content if present)
        extracted = await llm.ExtractAsync(request.Prompt, request.Files);

        // Post-processing: fix common LLM misclassifications
        var promptLower = request.Prompt.ToLowerInvariant();
        if (extracted.TaskType == "create_invoice" &&
            System.Text.RegularExpressions.Regex.IsMatch(promptLower,
                @"\b(innbetaling|betaling|payment|pago|pagamento|zahlung|paiement)\b"))
        {
            extracted.TaskType = "register_payment";
            logger.LogInformation("Overriding task_type from create_invoice to register_payment (payment keywords detected)");
        }
        if (extracted.TaskType == "create_invoice" &&
            System.Text.RegularExpressions.Regex.IsMatch(promptLower,
                @"\b(kreditnota|credit\s*note|nota de crédito|gutschrift|note de crédit)\b"))
        {
            extracted.TaskType = "create_credit_note";
            logger.LogInformation("Overriding task_type from create_invoice to create_credit_note (credit note keywords detected)");
        }

        // Detect "send" in invoice prompts
        if (extracted.TaskType == "create_invoice" &&
            System.Text.RegularExpressions.Regex.IsMatch(promptLower,
                @"\b(send|sende|enviar|envoyer|senden|versenden)\b"))
        {
            var inv = extracted.Entities.GetValueOrDefault("invoice") ?? new();
            inv["send"] = true;
            extracted.Entities["invoice"] = inv;
        }

        logger.LogInformation("Extracted task_type: {TaskType}, action: {Action}",
            extracted.TaskType, extracted.Action);

        // Step 2: Create API client — use request credentials, fall back to config (for local testing)
        var baseUrl = request.TripletexCredentials?.BaseUrl
            ?? app.Configuration["Tripletex:BaseUrl"]
            ?? throw new InvalidOperationException("No Tripletex base URL provided");
        var sessionToken = request.TripletexCredentials?.SessionToken
            ?? app.Configuration["Tripletex:SessionToken"]
            ?? throw new InvalidOperationException("No Tripletex session token provided");
        var apiLogger = app.Services.GetRequiredService<ILogger<TripletexApiClient>>();
        api = new TripletexApiClient(baseUrl, sessionToken, apiLogger);

        // Step 3: Route to handler
        var handled = await router.RouteAsync(api, extracted, request.Prompt, request.Files);

        if (!handled)
        {
            logger.LogWarning("No handler for task type: {TaskType} — returning completed anyway", extracted.TaskType);
        }

        sw.Stop();
        logger.LogInformation("Completed. API calls: {Calls}, errors: {Errors}, elapsed: {Elapsed}ms",
            api.CallCount, api.ErrorCount, sw.ElapsedMilliseconds);

        // Structured submission log (submissions.jsonl)
        LogSubmission(request, extracted, api, router.LastHandlerName, sw.ElapsedMilliseconds, success: true, error: null, httpContext);

        return Results.Json(new { status = "completed" });
    }
    catch (Exception ex)
    {
        sw.Stop();
        logger.LogError(ex, "Error processing /solve request");
        LogSubmission(request, extracted, api, router.LastHandlerName, sw.ElapsedMilliseconds, success: false, error: ex.Message, httpContext);
        return Results.Json(new { status = "completed" });
    }
});

app.Run();

static void LogSubmission(SolveRequest request, ExtractionResult? extracted, TripletexApiClient? api,
    string? handlerName, long elapsedMs, bool success, string? error, HttpContext? httpContext = null)
{
    try
    {
        var baseUrl = request.TripletexCredentials?.BaseUrl ?? "";
        var isForwarded = httpContext?.Request.Headers.ContainsKey("X-Forwarded-For") == true
                       || httpContext?.Request.Headers.ContainsKey("X-Forwarded-Host") == true;
        var env = (baseUrl.Contains("tx-proxy.ainm.no") || isForwarded) ? "competition" : "sandbox";
        var entry = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            environment = env,
            prompt = request.Prompt,
            files = request.Files?.Select(f => new { f.Filename, f.MimeType }).ToList(),
            task_type = extracted?.TaskType,
            action = extracted?.Action,
            language = extracted?.Language,
            handler = handlerName,
            entities = extracted?.Entities,
            success,
            error,
            elapsed_ms = elapsedMs,
            api_calls = api?.CallLog.Select(c => new { c.Method, c.Path, c.Status, c.Error }),
            call_count = api?.CallCount ?? 0,
            error_count = api?.ErrorCount ?? 0
        };
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
        Directory.CreateDirectory("logs");
        var logFile = env == "competition" ? "logs/submissions.jsonl" : "logs/sandbox.jsonl";
        File.AppendAllText(logFile, json + Environment.NewLine);
    }
    catch { /* never fail the response due to logging */ }
}
