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
        _fallback = new FallbackAgentHandler(githubToken, services.GetRequiredService<ILogger<FallbackAgentHandler>>());
    }

    public string GetHandlerName(string taskType)
    {
        if (_handlers.TryGetValue(taskType, out var handler))
            return handler.GetType().Name;
        return "FallbackAgentHandler";
    }

    public async Task<(bool handled, HandlerResult result)> RouteAsync(TripletexApiClient api, ExtractionResult extracted, string originalPrompt = "", List<Models.SolveFile>? files = null)
    {
        if (_handlers.TryGetValue(extracted.TaskType, out var handler))
        {
            LastHandlerName = handler.GetType().Name;
            _logger.LogInformation("Routing to deterministic handler for {TaskType} ({Handler})", extracted.TaskType, LastHandlerName);
            var result = await handler.HandleAsync(api, extracted);
            return (true, result);
        }

        LastHandlerName = "FallbackAgentHandler";
        _logger.LogInformation("No deterministic handler for {TaskType} — using fallback agent", extracted.TaskType);
        await _fallback.HandleWithPromptAsync(api, extracted, originalPrompt, files);
        return (true, HandlerResult.Empty);
    }
}
