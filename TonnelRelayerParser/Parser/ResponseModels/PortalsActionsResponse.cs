using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

public class Action
{
    [JsonPropertyName("nft")] public Nft? Nft { get; set; }

    [JsonPropertyName("offer_id")] public object? OfferId { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("amount")] public string? Amount { get; set; }

    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
}

public class Attribute
{
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("value")] public string? Value { get; set; }

    [JsonPropertyName("rarity_per_mille")] public double? RarityPerMille { get; set; }
}

public class Nft
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("photo_url")] public string? PhotoUrl { get; set; }

    [JsonPropertyName("collection_id")] public string? CollectionId { get; set; }

    [JsonPropertyName("external_collection_number")]
    public long? ExternalCollectionNumber { get; set; }

    [JsonPropertyName("price")] public string? Price { get; set; }

    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("animation_url")] public string? AnimationUrl { get; set; }

    [JsonPropertyName("has_animation")] public bool? HasAnimation { get; set; }

    [JsonPropertyName("attributes")] public Attribute[]? Attributes { get; set; }

    [JsonPropertyName("emoji_id")] public string? EmojiId { get; set; }

    [JsonPropertyName("is_owned")] public bool? IsOwned { get; set; }

    [JsonPropertyName("floor_price")] public string? FloorPrice { get; set; }
}

public class PortalsActionsResponse
{
    [JsonPropertyName("actions")] public Action[]? Actions { get; set; }
}