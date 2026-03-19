using System.Text.Json.Serialization;

namespace TripletexAgent.Models;

public class ExtractionResult
{
    [JsonPropertyName("task_type")]
    public string TaskType { get; set; } = "unknown";

    [JsonPropertyName("entities")]
    public Dictionary<string, Dictionary<string, object>> Entities { get; set; } = new();

    [JsonPropertyName("relationships")]
    public Dictionary<string, string> Relationships { get; set; } = new();

    [JsonPropertyName("action")]
    public string Action { get; set; } = "create";

    [JsonPropertyName("raw_amounts")]
    public List<string> RawAmounts { get; set; } = new();

    [JsonPropertyName("dates")]
    public List<string> Dates { get; set; } = new();

    [JsonPropertyName("files_needed")]
    public bool FilesNeeded { get; set; }
}
