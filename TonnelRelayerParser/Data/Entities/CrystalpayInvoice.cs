namespace Moahk.Data.Entities;

public class CrystalpayInvoice
{
    public string Id { get; init; }
    public string Url { get; init; }
    public int Days { get; init; }
    public bool IsPaid { get; set; } = false;
    public bool IsError { get; set; } = false;
    public User User { get; set; }
}