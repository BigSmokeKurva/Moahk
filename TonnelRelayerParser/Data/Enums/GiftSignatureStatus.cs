namespace Moahk.Data.Enums;

[Flags]
public enum GiftSignatureStatus : long
{
    Clean = 1L << 0,
    Dirty = 1L << 1
}