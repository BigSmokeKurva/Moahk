namespace Moahk.Data.Enums;

[Flags]
public enum Criteria : long
{
    SecondFloor = 1L << 0,
    SecondFloorWithoutBackdrop = 1L << 1
}