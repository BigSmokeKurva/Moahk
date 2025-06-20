using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

internal class RuCaptchaResultTaskResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }

    [JsonPropertyName("request")] public string? Request { get; set; }
}