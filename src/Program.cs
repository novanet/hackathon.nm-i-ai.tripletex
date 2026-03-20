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

app.MapPost("/solve", async (SolveRequest request, LlmExtractor llm, TaskRouter router, ILogger<Program> logger) =>
{
    logger.LogInformation("Received /solve request ({PromptLength} chars, {FileCount} files)",
        request.Prompt.Length, request.Files?.Count ?? 0);

    try
    {
        // Step 1: Extract structured data from prompt via LLM (with file content if present)
        var extracted = await llm.ExtractAsync(request.Prompt, request.Files);

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
        var api = new TripletexApiClient(baseUrl, sessionToken, apiLogger);

        // Step 3: Route to handler
        var handled = await router.RouteAsync(api, extracted, request.Prompt, request.Files);

        if (!handled)
        {
            logger.LogWarning("No handler for task type: {TaskType} — returning completed anyway", extracted.TaskType);
        }

        logger.LogInformation("Completed. API calls: {Calls}, errors: {Errors}",
            api.CallCount, api.ErrorCount);

        // Structured submission log (submissions.jsonl)
        LogSubmission(request, extracted, api);

        return Results.Json(new { status = "completed" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error processing /solve request");
        return Results.Json(new { status = "completed" });
    }
});

app.Run();

static void LogSubmission(SolveRequest request, ExtractionResult extracted, TripletexApiClient api)
{
    try
    {
        var baseUrl = request.TripletexCredentials?.BaseUrl ?? "";
        var env = baseUrl.Contains("tx-proxy.ainm.no") ? "competition" : "sandbox";
        var entry = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            environment = env,
            prompt = request.Prompt.Length > 500 ? request.Prompt[..500] : request.Prompt,
            files = request.Files?.Select(f => f.Filename).ToList() ?? new List<string>(),
            task_type = extracted.TaskType,
            action = extracted.Action,
            api_calls = api.CallLog.Select(c => new { c.Method, c.Path, c.Status, c.Error }),
            call_count = api.CallCount,
            error_count = api.ErrorCount
        };
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
        Directory.CreateDirectory("logs");
        var logFile = env == "competition" ? "logs/submissions.jsonl" : "logs/sandbox.jsonl";
        File.AppendAllText(logFile, json + Environment.NewLine);
    }
    catch { /* never fail the response due to logging */ }
}
