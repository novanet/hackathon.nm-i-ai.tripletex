using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TripletexAgent.Services;

public class ApiCallEntry
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public string? Error { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseSnippet { get; set; }
}

public class TripletexApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<TripletexApiClient> _logger;
    private int _callCount;
    private int _errorCount;
    private readonly List<ApiCallEntry> _callLog = new();

    public int CallCount => _callCount;
    public int ErrorCount => _errorCount;
    public IReadOnlyList<ApiCallEntry> CallLog => _callLog;
    private readonly int _sessionHash;
    public int SessionHash => _sessionHash;

    public TripletexApiClient(string baseUrl, string sessionToken, ILogger<TripletexApiClient> logger)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
        _sessionHash = sessionToken.GetHashCode();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var authBytes = Encoding.ASCII.GetBytes($"0:{sessionToken}");
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    public async Task<JsonElement> GetAsync(string path, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl(path, queryParams);
        return await SendAsync(HttpMethod.Get, url);
    }

    public async Task<JsonElement> PostAsync(string path, object body)
    {
        var url = BuildUrl(path);
        return await SendAsync(HttpMethod.Post, url, body);
    }

    public async Task<JsonElement> PostMultipartFileAsync(string path, byte[] fileBytes, string fileName, string contentType = "application/pdf")
    {
        Interlocked.Increment(ref _callCount);
        var url = BuildUrl(path);
        _logger.LogInformation("API POST {Path} (multipart: {FileName}, {Bytes} bytes)", path, fileName, fileBytes.Length);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var boundary = Guid.NewGuid().ToString("N");
        var multipart = new MultipartFormDataContent(boundary);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(fileContent, "file", fileName);
        request.Content = multipart;

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        var entry = new ApiCallEntry
        {
            Method = "POST",
            Path = path,
            Status = (int)response.StatusCode,
            RequestBody = $"[multipart: {fileName}]",
            ResponseSnippet = responseBody?.Length > 500 ? responseBody[..500] : responseBody
        };

        if (!response.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref _errorCount);
            entry.Error = responseBody;
            _callLog.Add(entry);
            _logger.LogWarning("API POST {Path} → {Status}: {Error}", path, (int)response.StatusCode, responseBody);
            throw new TripletexApiException((int)response.StatusCode, responseBody, responseBody);
        }

        _callLog.Add(entry);
        if (string.IsNullOrWhiteSpace(responseBody)) return default;
        return JsonDocument.Parse(responseBody).RootElement;
    }

    public async Task<JsonElement> PutAsync(string path, object? body = null, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl(path, queryParams);
        return await SendAsync(HttpMethod.Put, url, body);
    }

    public async Task<JsonElement> PostBankStatementImportAsync(long accountId, string fromDate, string toDate, string fileFormat, byte[] csvBytes, string fileName)
    {
        var path = $"/bank/statement/import?bankId=0&accountId={accountId}&fromDate={fromDate}&toDate={toDate}&fileFormat={fileFormat}";
        Interlocked.Increment(ref _callCount);
        var url = BuildUrl(path);
        _logger.LogInformation("API POST {Path} (multipart bank import: {FileName}, format={Format})", path, fileName, fileFormat);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var boundary = Guid.NewGuid().ToString("N");
        var multipart = new MultipartFormDataContent(boundary);
        var fileContent = new ByteArrayContent(csvBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
        multipart.Add(fileContent, "file", fileName);
        request.Content = multipart;

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        var entry = new ApiCallEntry
        {
            Method = "POST",
            Path = path,
            Status = (int)response.StatusCode,
            RequestBody = $"[multipart bank import: {fileName}, format={fileFormat}]",
            ResponseSnippet = responseBody?.Length > 500 ? responseBody[..500] : responseBody
        };

        if (!response.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref _errorCount);
            entry.Error = responseBody;
            _callLog.Add(entry);
            _logger.LogWarning("API POST bank import → {Status}: {Error}", (int)response.StatusCode, responseBody);
            throw new TripletexApiException((int)response.StatusCode, responseBody, responseBody);
        }

        _callLog.Add(entry);
        if (string.IsNullOrWhiteSpace(responseBody)) return default;
        return JsonDocument.Parse(responseBody).RootElement;
    }

    public async Task DeleteAsync(string path)
    {
        var url = BuildUrl(path);
        await SendAsync(HttpMethod.Delete, url);
    }

    private string BuildUrl(string path, Dictionary<string, string>? queryParams = null)
    {
        var url = $"{_baseUrl}{path}";
        if (queryParams is { Count: > 0 })
        {
            var qs = string.Join("&", queryParams.Select(
                kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url += $"?{qs}";
        }
        return url;
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, object? body = null)
    {
        Interlocked.Increment(ref _callCount);

        // Extract path from full URL for logging
        var path = url.StartsWith(_baseUrl) ? url[_baseUrl.Length..] : url;

        using var request = new HttpRequestMessage(method, url);
        string? requestBodyJson = null;
        if (body is not null)
        {
            requestBodyJson = JsonSerializer.Serialize(body);
            request.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");
        }

        _logger.LogInformation("API {Method} {Path}", method, path);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        var entry = new ApiCallEntry
        {
            Method = method.ToString(),
            Path = path,
            Status = (int)response.StatusCode,
            RequestBody = requestBodyJson,
            ResponseSnippet = responseBody?.Length > 500 ? responseBody[..500] : responseBody
        };

        if (!response.IsSuccessStatusCode)
        {
            // Capture longer response snippet for errors (validation messages are critical)
            entry.ResponseSnippet = responseBody?.Length > 2000 ? responseBody[..2000] : responseBody;
        }

        if (!response.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref _errorCount);
            string errorDetail = responseBody;
            try
            {
                var errDoc = JsonDocument.Parse(responseBody);
                var root = errDoc.RootElement;
                if (root.TryGetProperty("validationMessages", out var vms) && vms.GetArrayLength() > 0)
                {
                    var msgs = new List<string>();
                    foreach (var vm in vms.EnumerateArray())
                    {
                        var field = vm.TryGetProperty("field", out var f) ? f.GetString() : "";
                        var msg = vm.TryGetProperty("message", out var m) ? m.GetString() : "";
                        msgs.Add($"{field}: {msg}");
                    }
                    errorDetail = string.Join("; ", msgs);
                }
                else if (root.TryGetProperty("developerMessage", out var dm))
                {
                    errorDetail = dm.GetString() ?? responseBody;
                }
            }
            catch { }

            entry.Error = errorDetail;
            _callLog.Add(entry);

            _logger.LogWarning("API {Method} {Path} → {Status}: {Error}",
                method, path, (int)response.StatusCode, errorDetail);

            throw new TripletexApiException((int)response.StatusCode, errorDetail, responseBody);
        }

        _callLog.Add(entry);

        if (string.IsNullOrWhiteSpace(responseBody))
            return default;

        return JsonDocument.Parse(responseBody).RootElement;
    }
}

public class TripletexApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public TripletexApiException(int statusCode, string message, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
