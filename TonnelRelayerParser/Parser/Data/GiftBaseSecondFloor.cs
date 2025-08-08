namespace Moahk.Parser.Data;

public abstract class GiftBaseSecondFloor : GiftBase
{
    public SecondFloorGift? SecondFloorGift { get; init; }
}

public class SecondFloorGift
{
    public required TelegramGiftInfo TelegramGiftInfo { get; init; }
    public required double Price { get; init; }
    public required string BotUrl { get; init; }
}

public class TonnelGiftSecondFloor : GiftBaseSecondFloor
{
    public required string SiteUrl { get; init; }
}

public class PortalsGiftSecondFloor : GiftBaseSecondFloor;