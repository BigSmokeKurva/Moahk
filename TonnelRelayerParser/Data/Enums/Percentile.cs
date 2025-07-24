namespace Moahk.Data.Enums;

[Flags]
public enum Percentile : long
{
    None = 1L << 0,
    Percentile75 = 1L << 1,
    Percentile25 = 1L << 2
}