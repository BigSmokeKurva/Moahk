using Moahk.Data.Enums;

namespace Moahk.Data.Entities;

public class User
{
    public long Id { get; init; }
    public DateTimeOffset License { get; set; } = DateTimeOffset.MinValue;
    public double PriceMin { get; set; } = 0;
    public double PriceMax { get; set; } = 10000;
    public int ProfitPercent { get; set; } = 10;
    public Criteria Criteria { get; set; } = Criteria.Peak;
    public Status Status { get; set; } = Status.None;
    public bool IsStarted { get; set; } = false;
}