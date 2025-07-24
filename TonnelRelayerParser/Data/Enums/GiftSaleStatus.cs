namespace Moahk.Data.Enums;

[Flags]
public enum GiftSaleStatus : long
{
    NeverSold = 1L << 0,
    SoldBefore = 1L << 1
}