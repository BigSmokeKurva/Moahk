using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

public class TonnelSearch
{
    [JsonPropertyName("gift_num")] public double? GiftNum { get; set; }

    [JsonPropertyName("customEmojiId")] public string? CustomEmojiId { get; set; }

    [JsonPropertyName("gift_id")] public double? GiftId { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("model")] public string? Model { get; set; }

    [JsonPropertyName("asset")] public string? Asset { get; set; }

    [JsonPropertyName("symbol")] public string? Symbol { get; set; }

    [JsonPropertyName("backdrop")] public string? Backdrop { get; set; }

    [JsonPropertyName("availabilityIssued")]
    public double? AvailabilityIssued { get; set; }

    [JsonPropertyName("availabilityTotal")]
    public double? AvailabilityTotal { get; set; }

    [JsonPropertyName("message_in_channel")]
    public double? MessageInChannel { get; set; }

    [JsonPropertyName("price")] public double? Price { get; set; }

    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("limited")] public bool? Limited { get; set; }

    [JsonPropertyName("auction")] public object? Auction { get; set; }

    [JsonPropertyName("export_at")] public DateTimeOffset? ExportAt { get; set; }
}