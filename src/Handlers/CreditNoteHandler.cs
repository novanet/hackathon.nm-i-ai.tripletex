using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class CreditNoteHandler : ITaskHandler
{
    private readonly ILogger<CreditNoteHandler> _logger;
    private readonly InvoiceHandler _invoiceHandler;

    public CreditNoteHandler(ILogger<CreditNoteHandler> logger, InvoiceHandler invoiceHandler)
    {
        _logger = logger;
        _invoiceHandler = invoiceHandler;
    }

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var creditNote = extracted.Entities.GetValueOrDefault("creditNote") ?? new();
        var invoice = extracted.Entities.GetValueOrDefault("invoice") ?? new();

        // Try to get invoice ID directly
        var invoiceIdStr = GetStringField(creditNote, "invoiceId")
            ?? GetStringField(invoice, "id");

        long invoiceId;
        if (invoiceIdStr != null && long.TryParse(invoiceIdStr, out var parsedId))
        {
            invoiceId = parsedId;
        }
        else
        {
            // Need to create the full invoice chain first, then credit it
            _logger.LogInformation("No invoice ID found, creating invoice chain first");
            invoiceId = await _invoiceHandler.CreateInvoiceChainAsync(api, extracted);
        }

        // Build credit note query params
        var queryParams = new Dictionary<string, string>();

        var date = GetStringField(creditNote, "date")
            ?? (extracted.Dates.Count > 0 ? extracted.Dates[^1] : DateTime.Now.ToString("yyyy-MM-dd"));
        queryParams["date"] = date;

        var comment = GetStringField(creditNote, "comment");
        if (comment != null)
            queryParams["comment"] = comment;

        queryParams["sendToCustomer"] = "false";

        _logger.LogInformation("Creating credit note for invoice {InvoiceId} on {Date}", invoiceId, date);

        await api.PutAsync($"/invoice/{invoiceId}/:createCreditNote", null, queryParams);

        _logger.LogInformation("Credit note created for invoice {InvoiceId}", invoiceId);
    }

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
            return val is JsonElement je ? je.GetString() : val.ToString();
        return null;
    }
}
