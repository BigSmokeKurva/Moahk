using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

public class GiftBubblesDataGift
{
    [JsonPropertyName("change")] public double? Change { get; set; }

    [JsonPropertyName("change_7d")] public double? Change7d { get; set; }

    [JsonPropertyName("floorprice")] public double? Floorprice { get; set; }

    [JsonPropertyName("id")] public long? Id { get; set; }

    [JsonPropertyName("img_src")] public string? ImgSrc { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("volume")] public long? Volume { get; set; }
}