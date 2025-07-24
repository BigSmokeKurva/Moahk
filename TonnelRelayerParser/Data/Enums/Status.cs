namespace Moahk.Data.Enums;

[Flags]
public enum Status : long
{
    None = 1L << 0,
    WritingPriceRange = 1L << 1,
    WritingProfitPercent = 1L << 2,
    WritingModelPercent = 1L << 3,
    WritingPromoCode = 1L << 4
}