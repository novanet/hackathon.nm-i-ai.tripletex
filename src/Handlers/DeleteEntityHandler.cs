using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class DeleteEntityHandler : ITaskHandler
{
    private readonly ILogger<DeleteEntityHandler> _logger;

    public DeleteEntityHandler(ILogger<DeleteEntityHandler> logger) => _logger = logger;

    // Supported entity types and their API paths
    private static readonly Dictionary<string, string> EntityPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customer"] = "/customer",
        ["product"] = "/product",
        ["order"] = "/order",
        ["department"] = "/department",
        ["travelExpense"] = "/travelExpense",
        ["travel_expense"] = "/travelExpense",
        ["voucher"] = "/ledger/voucher",
        ["project"] = "/project",
        ["supplier"] = "/supplier",
    };

    public async Task HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        // Determine entity type from extraction
        var entityType = DetermineEntityType(extracted);
        if (entityType == null)
        {
            _logger.LogWarning("Could not determine entity type for deletion");
            return;
        }

        if (!EntityPaths.TryGetValue(entityType, out var basePath))
        {
            _logger.LogWarning("Unsupported entity type for deletion: {EntityType}", entityType);
            return;
        }

        var entity = extracted.Entities.GetValueOrDefault(entityType)
            ?? extracted.Entities.Values.FirstOrDefault()
            ?? new();

        // Try to get ID directly
        var idStr = GetStringField(entity, "id");
        if (idStr != null && long.TryParse(idStr, out var directId))
        {
            _logger.LogInformation("Deleting {EntityType} ID: {Id}", entityType, directId);
            await api.DeleteAsync($"{basePath}/{directId}");
            return;
        }

        // Search for the entity
        var entityId = await SearchEntity(api, basePath, entity, extracted);
        if (entityId.HasValue)
        {
            _logger.LogInformation("Deleting {EntityType} ID: {Id}", entityType, entityId.Value);
            await api.DeleteAsync($"{basePath}/{entityId.Value}");
        }
        else
        {
            _logger.LogWarning("Could not find {EntityType} to delete", entityType);
        }
    }

    private string? DetermineEntityType(ExtractionResult extracted)
    {
        // Check relationships for entity type hints
        if (extracted.Relationships.TryGetValue("entityType", out var relType))
            return relType;

        // Check which entity keys are present
        foreach (var key in extracted.Entities.Keys)
        {
            if (EntityPaths.ContainsKey(key))
                return key;
        }

        // Try to infer from action context
        if (extracted.Entities.ContainsKey("entity"))
        {
            var entity = extracted.Entities["entity"];
            if (entity.TryGetValue("type", out var typeVal))
                return typeVal?.ToString();
        }

        return null;
    }

    private async Task<long?> SearchEntity(TripletexApiClient api, string basePath, Dictionary<string, object> entity, ExtractionResult extracted)
    {
        var searchParams = new Dictionary<string, string>
        {
            ["count"] = "1",
            ["fields"] = "id,name"
        };

        var name = GetStringField(entity, "name");
        if (name != null)
            searchParams["name"] = name;

        try
        {
            var result = await api.GetAsync(basePath, searchParams);
            if (result.TryGetProperty("values", out var vals))
            {
                foreach (var v in vals.EnumerateArray())
                    return v.GetProperty("id").GetInt64();
            }
        }
        catch (TripletexApiException ex) when (ex.StatusCode == 400)
        {
            // Some endpoints don't support name search, try without
            _logger.LogDebug("Name search not supported for {Path}, trying without filter", basePath);
        }

        return null;
    }

    private static string? GetStringField(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
        {
            if (val is JsonElement je)
                return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
            return val.ToString();
        }
        return null;
    }
}
