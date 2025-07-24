namespace Moahk.Data.Enums;

[Flags]
public enum MessageType : long
{
    Full = 1L << 0,
    Compact = 1L << 1
}