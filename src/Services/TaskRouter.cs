using TripletexAgent.Handlers;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Services;

public class TaskRouter
{
    private readonly Dictionary<string, ITaskHandler> _handlers;
    private readonly FallbackAgentHandler _fallback;
    private readonly ILogger<TaskRouter> _logger;
    public string? LastHandlerName { get; private set; }

    public TaskRouter(IServiceProvider services, ILogger<TaskRouter> logger)
    {
        _logger = logger;
        _handlers = new Dictionary<string, ITaskHandler>
        {
            ["create_employee"] = services.GetRequiredService<EmployeeHandler>(),
            ["update_employee"] = services.GetRequiredService<EmployeeHandler>(),
            ["create_customer"] = services.GetRequiredService<CustomerHandler>(),
            ["create_product"] = services.GetRequiredService<ProductHandler>(),
            ["create_department"] = services.GetRequiredService<DepartmentHandler>(),
            ["create_supplier"] = services.GetRequiredService<SupplierHandler>(),
            ["create_invoice"] = services.GetRequiredService<InvoiceHandler>(),
            ["register_payment"] = services.GetRequiredService<PaymentHandler>(),
            ["create_project"] = services.GetRequiredService<ProjectHandler>(),
            ["create_travel_expense"] = services.GetRequiredService<TravelExpenseHandler>(),
            ["delete_travel_expense"] = services.GetRequiredService<TravelExpenseHandler>(),
            ["create_credit_note"] = services.GetRequiredService<CreditNoteHandler>(),
            ["create_voucher"] = services.GetRequiredService<VoucherHandler>(),
            ["delete_entity"] = services.GetRequiredService<DeleteEntityHandler>(),
            ["enable_module"] = services.GetRequiredService<EnableModuleHandler>(),
            ["run_payroll"] = services.GetRequiredService<PayrollHandler>(),
        };

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? services.GetRequiredService<IConfiguration>()["GitHubToken"]
            ?? "";
        var knowledge = services.GetRequiredService<TripletexKnowledgeService>();
        _fallback = new FallbackAgentHandler(githubToken, knowledge, services.GetRequiredService<ILogger<FallbackAgentHandler>>());
    }

    public string GetHandlerName(string taskType)
    {
        if (_handlers.TryGetValue(taskType, out var handler))
            return handler.GetType().Name;
        return "FallbackAgentHandler";
    }

    public async Task<(bool handled, HandlerResult result)> RouteAsync(TripletexApiClient api, ExtractionResult extracted, string originalPrompt = "", List<Models.SolveFile>? files = null)
    {
        var taskType = InferTaskType(extracted);

        if (_handlers.TryGetValue(taskType, out var handler))
        {
            LastHandlerName = handler.GetType().Name;
            _logger.LogInformation("Routing to deterministic handler for {TaskType} ({Handler})", taskType, LastHandlerName);
            var result = await handler.HandleAsync(api, extracted);
            return (true, result);
        }

        LastHandlerName = "FallbackAgentHandler";
        _logger.LogInformation("No deterministic handler for {TaskType} — using fallback agent", taskType);
        await _fallback.HandleWithPromptAsync(api, extracted, originalPrompt, files);
        return (true, HandlerResult.Empty);
    }

    private string InferTaskType(ExtractionResult extracted)
    {
        var tt = extracted.TaskType;

        // Check for project+invoice combo — should always go to ProjectHandler
        var hasProject = extracted.Entities.ContainsKey("project") && extracted.Entities["project"].Count > 0;
        var hasInvoice = extracted.Entities.ContainsKey("invoice") && extracted.Entities["invoice"].Count > 0;

        if (hasProject && (tt == "create_invoice" || !_handlers.ContainsKey(tt)))
        {
            _logger.LogInformation("Inferred task_type create_project from project entity (was {Original})", tt);
            extracted.TaskType = "create_project";
            return "create_project";
        }

        // If we already have a mapped handler, use it
        if (_handlers.ContainsKey(tt))
            return tt;

        // Infer from entities when task_type is unknown
        if (hasProject)
        {
            _logger.LogInformation("Inferred task_type create_project (was {Original})", tt);
            extracted.TaskType = "create_project";
            return "create_project";
        }

        var hasVoucher = extracted.Entities.ContainsKey("voucher") && extracted.Entities["voucher"].Count > 0;
        if (hasVoucher)
        {
            _logger.LogInformation("Inferred task_type create_voucher (was {Original})", tt);
            extracted.TaskType = "create_voucher";
            return "create_voucher";
        }

        var hasPayroll = extracted.Entities.ContainsKey("payroll") && extracted.Entities["payroll"].Count > 0;
        if (hasPayroll)
        {
            _logger.LogInformation("Inferred task_type run_payroll (was {Original})", tt);
            extracted.TaskType = "run_payroll";
            return "run_payroll";
        }

        return tt;
    }
}
