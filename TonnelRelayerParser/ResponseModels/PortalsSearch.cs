using System.Text.Json.Serialization;

namespace Moahk.ResponseModels;

public class PortalsSearch
{
    [JsonPropertyName("results")] public Result[]? Results { get; set; }

    public class Attribute
    {
        [JsonPropertyName("type")] public string? Type { get; set; }

        [JsonPropertyName("value")] public string? Value { get; set; }

        [JsonPropertyName("rarity_per_mille")] public double? RarityPerMille { get; set; }
    }

    public class Result
    {
        [JsonPropertyName("id")] public string? Id { get; set; }

        [JsonPropertyName("tg_id")] public string? TgId { get; set; }

        [JsonPropertyName("collection_id")] public string? CollectionId { get; set; }

        [JsonPropertyName("external_collection_number")]
        public long? ExternalCollectionNumber { get; set; }

        [JsonPropertyName("owner_id")] public object? OwnerId { get; set; }

        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("photo_url")] public string? PhotoUrl { get; set; }

        [JsonPropertyName("price")] public string? Price { get; set; }

        [JsonPropertyName("attributes")] public Attribute[]? Attributes { get; set; }

        [JsonPropertyName("listed_at")] public DateTimeOffset? ListedAt { get; set; }

        [JsonPropertyName("status")] public string? Status { get; set; }

        [JsonPropertyName("animation_url")] public string? AnimationUrl { get; set; }

        [JsonPropertyName("emoji_id")] public string? EmojiId { get; set; }

        [JsonPropertyName("has_animation")] public bool? HasAnimation { get; set; }

        [JsonPropertyName("floor_price")] public string? FloorPrice { get; set; }

        [JsonPropertyName("unlocks_at")] public DateTimeOffset? UnlocksAt { get; set; }
    }
}