using System.Text.Json.Serialization;

namespace Moahk.Parser.ResponseModels;

public class CrystalpayInvoiceInfoResponse
{
    [JsonPropertyName("error")] public bool Error { get; set; }

    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("state")] public string? State { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("method")] public string? Method { get; set; }

    [JsonPropertyName("amount_currency")] public string? AmountCurrency { get; set; }

    [JsonPropertyName("rub_amount")] public string? RubAmount { get; set; }

    [JsonPropertyName("initial_amount")] public string? InitialAmount { get; set; }

    [JsonPropertyName("remaining_amount")] public string? RemainingAmount { get; set; }

    [JsonPropertyName("balance_amount")] public string? BalanceAmount { get; set; }

    [JsonPropertyName("commission_amount")]
    public string? CommissionAmount { get; set; }

    [JsonPropertyName("description")] public object? Description { get; set; }

    [JsonPropertyName("redirect_url")] public string? RedirectUrl { get; set; }

    [JsonPropertyName("callback_url")] public object? CallbackUrl { get; set; }

    [JsonPropertyName("extra")] public object? Extra { get; set; }

    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }

    [JsonPropertyName("expired_at")] public string? ExpiredAt { get; set; }

    [JsonPropertyName("final_at")] public string? FinalAt { get; set; }
}