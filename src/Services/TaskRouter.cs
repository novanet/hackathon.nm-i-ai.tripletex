using TripletexAgent.Handlers;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Services;

public class TaskRouter
{
    private readonly Dictionary<string, ITaskHandler> _handlers;
    private readonly FallbackAgentHandler _fallback;
    private readonly ILogger<TaskRouter> _logger;

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
        };

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? services.GetRequiredService<IConfiguration>()["GitHubToken"]
            ?? "";
        _fallback = new FallbackAgentHandler(githubToken, services.GetRequiredService<ILogger<FallbackAgentHandler>>());
    }

    public async Task<bool> RouteAsync(TripletexApiClient api, ExtractionResult extracted, string originalPrompt = "")
    {
        if (_handlers.TryGetValue(extracted.TaskType, out var handler))
        {
            _logger.LogInformation("Routing to deterministic handler for {TaskType}", extracted.TaskType);
            await handler.HandleAsync(api, extracted);
            return true;
        }

        _logger.LogInformation("No deterministic handler for {TaskType} — using fallback agent", extracted.TaskType);
        await _fallback.HandleWithPromptAsync(api, extracted, originalPrompt);
        return true;
    }
}
