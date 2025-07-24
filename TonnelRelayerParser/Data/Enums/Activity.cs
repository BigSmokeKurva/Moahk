namespace Moahk.Data.Enums;

[Flags]
public enum Activity : long
{
    Low = 1L << 0,
    Medium = 1L << 1,
    High = 1L << 2
}