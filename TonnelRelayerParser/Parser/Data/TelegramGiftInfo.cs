namespace Moahk.Parser.Data;

public struct TelegramGiftInfo
{
    public string Collection;
    public (string, double) Model;
    public (string, double) Backdrop;
    public (string, double) Symbol;
    public (int Issued, int All) Quantity;
    public bool Signature;
    public string Id;
}