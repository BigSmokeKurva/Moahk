namespace Moahk.Data.Entities;

public class CrystalpayInvoice
{
    public string Id { get; init; }
    public string Url { get; init; }
    public int Days { get; init; }
    public bool IsPaid { get; set; }
    public bool IsError { get; set; }
    public double Amount { get; set; }
    public virtual User User { get; set; }
}