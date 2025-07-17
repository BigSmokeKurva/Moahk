namespace Moahk.Parser;

public struct TelegramGiftInfo
{
    public (string, double) Model;
    public (string, double) Backdrop;
    public (string, double) Symbol;
    public (int Issued, int All) Quantity;
    public bool IsSold;
    public string Id;
}