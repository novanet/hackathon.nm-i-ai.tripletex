using System.Text.Json;
using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public class EnableModuleHandler : ITaskHandler
{
    private readonly ILogger<EnableModuleHandler> _logger;

    // Map common module name variants (from prompts in any language) to API enum values
    private static readonly Dictionary<string, string> ModuleNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Department/project modules
        ["department"] = "PROJECT",
        ["avdeling"] = "PROJECT",
        ["abteilung"] = "PROJECT",
        ["departamento"] = "PROJECT",
        ["département"] = "PROJECT",
        ["project"] = "PROJECT",
        ["prosjekt"] = "PROJECT",
        ["proyecto"] = "PROJECT",
        ["projeto"] = "PROJECT",
        ["projekt"] = "PROJECT",
        ["projet"] = "PROJECT",

        // Time tracking
        ["time_tracking"] = "SMART_TIME_TRACKING",
        ["timetracking"] = "SMART_TIME_TRACKING",
        ["tidregistrering"] = "SMART_TIME_TRACKING",
        ["tidsregistrering"] = "SMART_TIME_TRACKING",
        ["zeiterfassung"] = "SMART_TIME_TRACKING",

        // Wage / salary
        ["wage"] = "SMART_WAGE",
        ["lønn"] = "SMART_WAGE",
        ["salary"] = "SMART_WAGE",
        ["gehalt"] = "SMART_WAGE",
        ["salaire"] = "SMART_WAGE",
        ["salario"] = "SMART_WAGE",

        // OCR
        ["ocr"] = "OCR",

        // Logistics
        ["logistics"] = "LOGISTICS",
        ["logistikk"] = "LOGISTICS",

        // Electronic vouchers
        ["electronic_vouchers"] = "ELECTRONIC_VOUCHERS",
        ["elektroniske_bilag"] = "ELECTRONIC_VOUCHERS",

        // API access
        ["api"] = "API_V2",
        ["api_v2"] = "API_V2",
    };

    public EnableModuleHandler(ILogger<EnableModuleHandler> logger) => _logger = logger;

    public async Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted)
    {
        var module = extracted.Entities.GetValueOrDefault("module") ?? new();

        var moduleName = GetStringField(module, "name") ?? GetStringField(module, "moduleName")
            ?? GetStringField(module, "module_name") ?? "";

        // Try to resolve to API enum value
        var apiModuleName = ResolveModuleName(moduleName);

        var date = extracted.Dates.Count > 0 ? extracted.Dates[0] : DateTime.Now.ToString("yyyy-MM-dd");

        var body = new Dictionary<string, object>
        {
            ["name"] = apiModuleName,
            ["costStartDate"] = date
        };

        _logger.LogInformation("Enabling module: {Module} (resolved: {Resolved})", moduleName, apiModuleName);

        await api.PostAsync("/company/salesmodules", body);

        _logger.LogInformation("Module {Module} enabled", apiModuleName);
        return new HandlerResult { EntityType = "module", Metadata = { ["moduleName"] = apiModuleName } };
    }

    private static string ResolveModuleName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "PROJECT";

        // Direct match to known API enum value (already uppercase)
        var upper = input.Trim().ToUpperInvariant().Replace(" ", "_");
        var knownModules = new HashSet<string>
        {
            "PROJECT", "SMART_PROJECT", "WAGE", "SMART_WAGE", "TIME_TRACKING", "SMART_TIME_TRACKING",
            "OCR", "LOGISTICS", "ELECTRONIC_VOUCHERS", "API_V2", "BASIS", "SMART", "KOMPLETT",
            "PRO", "FIXED_ASSETS_REGISTER"
        };
        if (knownModules.Contains(upper)) return upper;

        // Try our mapping
        if (ModuleNameMap.TryGetValue(input.Trim(), out var mapped)) return mapped;
        if (ModuleNameMap.TryGetValue(upper, out var mapped2)) return mapped2;

        // Fallback: return as-is (let the API validate)
        return upper;
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
