using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

internal class RuCaptchaCreateTaskResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }

    [JsonPropertyName("request")] public string Request { get; set; }
}