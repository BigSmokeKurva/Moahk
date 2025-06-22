using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

public class CrystalpayInvoiceCreateResponse
{
    [JsonPropertyName("error")] public bool Error { get; set; }

    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("rub_amount")] public string? RubAmount { get; set; }
}