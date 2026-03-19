using TripletexAgent.Handlers;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Services;

public class TaskRouter
{
    private readonly Dictionary<string, ITaskHandler> _handlers;
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
        };
    }

    public async Task<bool> RouteAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        if (_handlers.TryGetValue(extracted.TaskType, out var handler))
        {
            _logger.LogInformation("Routing to deterministic handler for {TaskType}", extracted.TaskType);
            await handler.HandleAsync(api, extracted);
            return true;
        }

        _logger.LogWarning("No handler found for task type: {TaskType}", extracted.TaskType);
        return false;
    }
}
