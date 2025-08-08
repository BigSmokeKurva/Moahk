using Moahk.Data.Enums;

namespace Moahk.Parser.Data;

public abstract class GiftBase
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required string Backdrop { get; init; }
    public required Activity Activity { get; init; }
    public required double Price { get; init; }
    public required string TelegramGiftId { get; init; }
    public required string BotUrl { get; init; }
    public required TelegramGiftInfo TelegramGiftInfo { get; init; }
    public double? Percentile25 { get; set; }
    public double? Percentile75 { get; set; }
    public Action[]? ActivityHistory7Days { get; init; }
    public Action[]? ActivityHistoryAll { get; init; }
}