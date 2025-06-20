using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

public class Cookie
{
    [JsonPropertyName("domain")] public string? Domain { get; set; }

    [JsonPropertyName("expiry")] public int? Expiry { get; set; }

    [JsonPropertyName("httpOnly")] public bool? HttpOnly { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("path")] public string? Path { get; set; }

    [JsonPropertyName("sameSite")] public string? SameSite { get; set; }

    [JsonPropertyName("secure")] public bool? Secure { get; set; }

    [JsonPropertyName("value")] public string? Value { get; set; }
}

public class SolveCloudflareResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonPropertyName("solution")] public Solution? Solution { get; set; }

    [JsonPropertyName("startTimestamp")] public long? StartTimestamp { get; set; }

    [JsonPropertyName("endTimestamp")] public long? EndTimestamp { get; set; }

    [JsonPropertyName("version")] public string? Version { get; set; }
}

public class Solution
{
    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("status")] public int? Status { get; set; }

    [JsonPropertyName("cookies")] public Cookie[]? Cookies { get; set; }

    [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }

    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("response")] public string? Response { get; set; }
}