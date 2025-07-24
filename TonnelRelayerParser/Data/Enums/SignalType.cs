namespace Moahk.Data.Enums;

[Flags]
public enum SignalType : long
{
    TonnelTonnel = 1L << 0,
    TonnelPortals = 1L << 1,
    PortalsPortals = 1L << 2,
    PortalsTonnel = 1L << 3
}