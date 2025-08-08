namespace Moahk.Data.Enums;

[Flags]
public enum Criterion : long
{
    SecondFloor = 1L << 0,
    SecondFloorWithoutBackdrop = 1L << 1,
    Percentile25WithoutBackdrop = 1L << 2,
    ArithmeticMeanThree = 1L << 3
}