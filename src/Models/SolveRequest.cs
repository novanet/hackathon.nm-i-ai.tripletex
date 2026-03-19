using System.Text.Json.Serialization;

namespace TripletexAgent.Models;

public class SolveRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("files")]
    public List<SolveFile>? Files { get; set; }

    [JsonPropertyName("tripletex_credentials")]
    public TripletexCredentials? TripletexCredentials { get; set; }
}

public class SolveFile
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("content_base64")]
    public string ContentBase64 { get; set; } = "";

    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "";
}

public class TripletexCredentials
{
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("session_token")]
    public string SessionToken { get; set; } = "";
}
