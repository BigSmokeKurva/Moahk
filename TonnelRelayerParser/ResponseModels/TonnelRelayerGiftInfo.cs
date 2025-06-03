using System.Text.Json.Serialization;

namespace Moahk.ResponseModels;

public class TonnelRelayerGiftInfo
{
    [JsonPropertyName("gift_num")] public long? GiftNum { get; set; }
    [JsonPropertyName("customEmojiId")] public string? CustomEmojiId { get; set; }
    [JsonPropertyName("gift_id")] public long GiftId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("asset")] public string? Asset { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("backdrop")] public string? Backdrop { get; set; }

    [JsonPropertyName("availabilityIssued")]
    public object? AvailabilityIssued { get; set; }

    [JsonPropertyName("availabilityTotal")]
    public object? AvailabilityTotal { get; set; }

    [JsonPropertyName("message_in_channel")]
    public object? MessageInChannel { get; set; }

    [JsonPropertyName("price")] public double? Price { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("limited")] public bool? Limited { get; set; }
    [JsonPropertyName("auction")] public object? Auction { get; set; }
    [JsonPropertyName("export_at")] public DateTimeOffset? ExportAt { get; set; }
    [JsonPropertyName("premarket")] public bool? Premarket { get; set; }
    [JsonPropertyName("premarketStage")] public long? PremarketStage { get; set; }
}