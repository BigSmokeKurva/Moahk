using Moahk.Data.Enums;
using Moahk.Parser.ResponseModels;

namespace Moahk.Parser.Data;

public class GiftSecondFloorCriterion
{
    public GiftBubblesDataGift? BubblesDataGift { get; init; }
    public SignalType? Type { get; set; }
    public double PercentDiff { get; set; }
    public double? PercentDiffWithCommission { get; set; }
    public TonnelGiftSecondFloor? TonnelGift { get; init; }
    public PortalsGiftSecondFloor? PortalsGift { get; init; }
}