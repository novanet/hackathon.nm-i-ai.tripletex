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
builder.Services.AddSingleton<PayrollHandler>();
builder.Services.AddSingleton<TaskRouter>();
builder.Services.AddSingleton<SandboxValidator>();
builder.Services.AddSingleton<TripletexKnowledgeService>();

// LLM extractor
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
    ?? builder.Configuration["GitHubToken"]
    ?? throw new InvalidOperationException("GITHUB_TOKEN environment variable (or GitHubToken in appsettings) is required");
builder.Services.AddSingleton(sp => new LlmExtractor(githubToken, sp.GetRequiredService<ILogger<LlmExtractor>>()));

var isDryRun = string.Equals(Environment.GetEnvironmentVariable("DRY_RUN"), "true", StringComparison.OrdinalIgnoreCase);

var app = builder.Build();

if (isDryRun)
    app.Logger.LogWarning("DRY_RUN mode enabled — no API calls will be made, returning bare 200");

// API key auth middleware — disabled for competition (platform sends no key)
// var apiKey = app.Configuration["ApiKey"] ?? Environment.GetEnvironmentVariable("API_KEY");

app.MapGet("/", () => "Tripletex Agent is running");

app.MapPost("/solve", async (HttpContext httpContext, SolveRequest request, LlmExtractor llm, TaskRouter router, SandboxValidator validator, ILogger<Program> logger) =>
{
    var sw = Stopwatch.StartNew();
    logger.LogInformation("Received /solve request ({PromptLength} chars, {FileCount} files)",
        request.Prompt.Length, request.Files?.Count ?? 0);

    ExtractionResult? extracted = null;
    TripletexApiClient? api = null;
    string? handlerName = null;

    try
    {
        // Fast-path for health check pings — skip LLM entirely
        if (string.Equals(request.Prompt?.Trim(), "ping", StringComparison.OrdinalIgnoreCase))
        {
            sw.Stop();
            logger.LogInformation("Health check ping — returning completed in {Elapsed}ms", sw.ElapsedMilliseconds);
            return Results.Json(new { status = "completed" });
        }

        // Step 1: Extract structured data from prompt via LLM (with file content if present)
        extracted = await llm.ExtractAsync(request.Prompt, request.Files);

        // Post-processing: fix common LLM misclassifications
        var promptLower = request.Prompt.ToLowerInvariant();

        // Override to create_employee if LLM missed it (covers unknown, misclassified travel/voucher, etc.)
        if (extracted.TaskType is not "create_employee" and not "update_employee" and not "register_payment" and not "run_payroll" &&
            System.Text.RegularExpressions.Regex.IsMatch(promptLower,
                @"\b(ny\s+(?:ansatt|tilsett)|new\s+employee|nuevo\s+empleado|neuer?\s+mitarbeiter|nouvel?\s+employ[ée]|novo\s+(?:empregado|funcion[áa]rio))\b") &&
            !System.Text.RegularExpressions.Regex.IsMatch(promptLower,
                @"\b(reise|travel|viaje|viagem|voyage|reiserekning|reiseregning)\b"))
        {
            extracted.TaskType = "create_employee";
            logger.LogInformation("Overriding task_type to create_employee (strong employee-creation keywords detected, was {Original})", extracted.TaskType);
        }
        // Weaker fallback: single keyword match only for truly unknown tasks
        if (extracted.TaskType is "unknown" or "create_contact" &&
            System.Text.RegularExpressions.Regex.IsMatch(promptLower,
                @"\b(ansatt|tilsett|employee|empleado|funcionário|funcionario|mitarbeiter|employé|empregado)\b"))
        {
            extracted.TaskType = "create_employee";
            logger.LogInformation("Overriding task_type to create_employee (employee keywords detected)");
        }

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

        // Post-processing: ensure employee has firstName/lastName and dateOfBirth
        if (extracted.TaskType is "create_employee" or "update_employee")
        {
            var emp = extracted.Entities.GetValueOrDefault("employee") ?? new();
            if (!emp.ContainsKey("firstName") && !emp.ContainsKey("lastName") && !emp.ContainsKey("name"))
            {
                var nameMatch = System.Text.RegularExpressions.Regex.Match(request.Prompt,
                    @"(?:navn|name|nombre|nom|Nome|namens|llamad[oa]|nommée?|chamad[oa]|heiter|heter)\s+'?([A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+(?:\s+[A-Z\u00C0-\u017F][a-z\u00E0-\u017F]+)+)",
                    System.Text.RegularExpressions.RegexOptions.None);
                if (nameMatch.Success)
                {
                    var parts = nameMatch.Groups[1].Value.Trim().Split(' ', 2);
                    emp["firstName"] = parts[0];
                    emp["lastName"] = parts.Length > 1 ? parts[1] : parts[0];
                    logger.LogInformation("Post-processing: extracted employee name {First} {Last}", parts[0], parts.Length > 1 ? parts[1] : parts[0]);
                }
            }
            if (!emp.ContainsKey("dateOfBirth"))
            {
                var dobMatch = System.Text.RegularExpressions.Regex.Match(request.Prompt,
                    @"(?:født|fødd|born|geboren|nascid[oa]|nacid[oa]|né[e]?|fødselsdato|date\s*of\s*birth)\s*(?:el\s*|em\s*|am\s*|den\s*|le\s*|:?\s*)(\d{1,2})[.\s]+(\w+)\s+(\d{4})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (dobMatch.Success)
                {
                    var dateStr = LlmExtractor.TryParseDate(dobMatch);
                    if (dateStr != null)
                    {
                        emp["dateOfBirth"] = dateStr;
                        logger.LogInformation("Post-processing: extracted dateOfBirth {Date}", dateStr);
                    }
                }
            }
            if (!emp.ContainsKey("startDate"))
            {
                var sdMatch = System.Text.RegularExpressions.Regex.Match(request.Prompt,
                    @"(?:startdato|startdatum|start\s*date|fecha\s*de\s*inicio|data\s*de\s*início|date\s*de\s*début|début)\s*(?:el\s*|am\s*|den\s*|le\s*|:?\s*)(\d{1,2})[.\s]+(\w+)\s+(\d{4})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (sdMatch.Success)
                {
                    var dateStr = LlmExtractor.TryParseDate(sdMatch);
                    if (dateStr != null)
                    {
                        emp["startDate"] = dateStr;
                        logger.LogInformation("Post-processing: extracted startDate {Date}", dateStr);
                    }
                }
            }
            // Extract email if missing
            if (!emp.ContainsKey("email"))
            {
                var emailMatch = System.Text.RegularExpressions.Regex.Match(request.Prompt,
                    @"[\w.+-]+@[\w.-]+\.\w{2,}");
                if (emailMatch.Success)
                {
                    emp["email"] = emailMatch.Value;
                    logger.LogInformation("Post-processing: extracted email {Email}", emailMatch.Value);
                }
            }
            extracted.Entities["employee"] = emp;
        }

        logger.LogInformation("Extracted task_type: {TaskType}, action: {Action}",
            extracted.TaskType, extracted.Action);

        // Dry-run mode: log extraction, skip API calls, return bare 200
        if (isDryRun)
        {
            sw.Stop();
            var dryRunHandler = router.GetHandlerName(extracted.TaskType);
            logger.LogInformation("DRY_RUN: would route to {Handler} for {TaskType} — skipping execution ({Elapsed}ms)",
                dryRunHandler, extracted.TaskType, sw.ElapsedMilliseconds);
            LogRecon(request, extracted, dryRunHandler, sw.ElapsedMilliseconds);
            return Results.Ok();
        }

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
        var (handled, handlerResult, routedHandlerName) = await router.RouteAsync(api, extracted, request.Prompt, request.Files);
        handlerName = routedHandlerName;

        if (!handled)
        {
            logger.LogWarning("No handler for task type: {TaskType} — returning completed anyway", extracted.TaskType);
        }

        sw.Stop();
        logger.LogInformation("Completed. API calls: {Calls}, errors: {Errors}, elapsed: {Elapsed}ms",
            api.CallCount, api.ErrorCount, sw.ElapsedMilliseconds);

        // Run sandbox validation (only for non-competition requests)
        var isCompetition = baseUrl.Contains("tx-proxy.ainm.no")
            || httpContext.Request.Headers.ContainsKey("X-Forwarded-For");
        if (!isCompetition && handlerResult.EntityId.HasValue)
        {
            try
            {
                var validationReport = await validator.ValidateAsync(api, extracted, handlerResult);
                LogValidation(request, extracted, validationReport, api.CallCount, sw.ElapsedMilliseconds);
            }
            catch (Exception vex)
            {
                logger.LogWarning(vex, "Validation check failed (non-fatal)");
            }
        }

        // Structured submission log (submissions.jsonl)
        LogSubmission(request, extracted, api, handlerName, sw.ElapsedMilliseconds, success: true, error: null, httpContext);

        return Results.Json(new { status = "completed" });
    }
    catch (Exception ex)
    {
        sw.Stop();
        logger.LogError(ex, "Error processing /solve request");
        LogSubmission(request, extracted, api, handlerName ?? router.GetHandlerName(extracted?.TaskType ?? "unknown"), sw.ElapsedMilliseconds, success: false, error: ex.Message, httpContext);
        return Results.Json(new { status = "completed" });
    }
});

app.Run();

static void LogRecon(SolveRequest request, ExtractionResult extracted, string handlerName, long elapsedMs)
{
    try
    {
        var entry = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            prompt = request.Prompt,
            files = request.Files?.Select(f => new { f.Filename, f.MimeType }).ToList(),
            task_type = extracted.TaskType,
            action = extracted.Action,
            language = extracted.Language,
            handler = handlerName,
            entities = extracted.Entities,
            relationships = extracted.Relationships,
            dates = extracted.Dates,
            elapsed_ms = elapsedMs
        };
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
        Directory.CreateDirectory("logs");
        File.AppendAllText("logs/recon.jsonl", json + Environment.NewLine);
    }
    catch { /* never fail the response due to logging */ }
}

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

static void LogValidation(SolveRequest request, ExtractionResult extracted,
    ValidationReport validationReport, int apiCalls, long elapsedMs)
{
    try
    {
        var entry = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            prompt = request.Prompt,
            task_type = extracted.TaskType,
            entity_type = validationReport.EntityType,
            entity_id = validationReport.EntityId,
            correctness = validationReport.Correctness,
            points_earned = validationReport.PointsEarned,
            max_points = validationReport.MaxPoints,
            checks = validationReport.Checks.Select(c => new
            {
                field = c.Field,
                expected = c.Expected,
                actual = c.Actual,
                passed = c.Passed,
                points = c.Points
            }),
            api_calls = apiCalls,
            elapsed_ms = elapsedMs,
            error = validationReport.Error
        };
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
        Directory.CreateDirectory("logs");
        File.AppendAllText("logs/validations.jsonl", json + Environment.NewLine);
    }
    catch { /* never fail the response due to logging */ }
}
