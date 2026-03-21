using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

/// <summary>
/// Handles Tasks 20 + 22: Extract voucher/invoice data from a PDF document (supplier invoice, receipt, etc.)
/// and create the corresponding voucher in Tripletex.
///
/// Two task types route here:
/// - "extract_supplier_invoice_pdf" — supplier invoice PDF (leverandørfaktura)
/// - "extract_voucher_receipt_pdf"  — receipt/expense voucher PDF (kvittering/bilag)
///
/// The LLM already extracts the voucher fields from the attached PDF text/image.
/// This handler normalizes the task type and delegates to VoucherHandler, which has
/// full support for supplier invoice detection (supplierName/supplierOrgNumber fields)
/// and standard voucher creation.
/// </summary>
public class PdfVoucherHandler : ITaskHandler
{
    private readonly VoucherHandler _voucherHandler;
    private readonly ILogger<PdfVoucherHandler> _logger;

    public PdfVoucherHandler(VoucherHandler voucherHandler, ILogger<PdfVoucherHandler> logger)
    {
        _voucherHandler = voucherHandler;
        _logger = logger;
    }

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        _logger.LogInformation("PdfVoucherHandler: delegating to VoucherHandler (task_type was '{Type}', files={Files})",
            extracted.TaskType, extracted.Files?.Count ?? 0);

        // Normalize task type so VoucherHandler processes it as a voucher creation
        extracted.TaskType = "create_voucher";
        extracted.Action = "create";

        return await _voucherHandler.HandleAsync(api, extracted);
    }
}
