using System.Text.Json.Serialization;

namespace Moahk.ResponseModels;

public class TonnelRelayerHistoryGiftInfo
{
    [JsonPropertyName("_id")] public string? Id { get; set; }
    [JsonPropertyName("gift_id")] public long? GiftId { get; set; }
    [JsonPropertyName("gift_num")] public long? GiftNum { get; set; }
    [JsonPropertyName("gift_name")] public string? GiftName { get; set; }
    [JsonPropertyName("bidder")] public long? Bidder { get; set; }
    [JsonPropertyName("price")] public double? Price { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("backdrop")] public string? Backdrop { get; set; }
    [JsonPropertyName("asset")] public string? Asset { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("__v")] public long? V { get; set; }
}