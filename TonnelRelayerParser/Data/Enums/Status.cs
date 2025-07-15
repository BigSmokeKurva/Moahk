namespace Moahk.Data.Enums;

[Flags]
public enum Status : long
{
    None = 0L,
    WritingPriceRange = 1L << 0,
    WritingProfitPercent = 1L << 1,
    WritingModelPercent = 1L << 2,
    WritingPromoCode = 1L << 3
}