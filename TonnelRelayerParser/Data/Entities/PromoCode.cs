namespace Moahk.Data.Entities;

public class PromoCode
{
    public required string Code { get; init; }
    public double Percent { get; init; }
    public int? MaxUses { get; init; }
    public DateTimeOffset? DateExpiration { get; init; }
    public List<long> UsedUsersIds { get; init; } = [];
}