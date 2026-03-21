using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles Tasks 19 + 21: Extract employee data from a PDF document (offer letter, contract, etc.)
/// and create the employee in Tripletex.
///
/// Two task types route here:
/// - "extract_employee_pdf"       — generic employee PDF extraction
/// - "extract_employee_offer_pdf" — employment offer letter specifically
///
/// The LLM already extracts the employee fields from the attached PDF text/image.
/// This handler's job is to ensure the task_type is correctly remapped and then
/// delegate execution to the standard EmployeeHandler.
/// </summary>
public class PdfEmployeeHandler : ITaskHandler
{
    private readonly EmployeeHandler _employeeHandler;
    private readonly ILogger<PdfEmployeeHandler> _logger;

    public PdfEmployeeHandler(EmployeeHandler employeeHandler, ILogger<PdfEmployeeHandler> logger)
    {
        _employeeHandler = employeeHandler;
        _logger = logger;
    }

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        _logger.LogInformation("PdfEmployeeHandler: delegating to EmployeeHandler (task_type was '{Type}', files={Files})",
            extracted.TaskType, extracted.Files?.Count ?? 0);

        // Normalize task type so EmployeeHandler processes it as create
        extracted.TaskType = "create_employee";
        extracted.Action = "create";

        return await _employeeHandler.HandleAsync(api, extracted);
    }
}
