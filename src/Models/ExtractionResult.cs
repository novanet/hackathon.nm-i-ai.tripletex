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

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Original prompt text (set by LlmExtractor, not from JSON)</summary>
    [JsonIgnore]
    public string? RawPrompt { get; set; }

    /// <summary>Attached files from the request (set by Program.cs, not from JSON)</summary>
    [JsonIgnore]
    public List<SolveFile>? Files { get; set; }
}
