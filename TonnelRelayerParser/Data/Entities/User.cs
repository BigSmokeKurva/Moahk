using Moahk.Data.Enums;

namespace Moahk.Data.Entities;

public class User
{
    public long Id { get; init; }
    public DateTimeOffset? License { get; set; }
    public double PriceMin { get; set; }
    public double PriceMax { get; set; } = 10000;
    public int ProfitPercent { get; set; } = 10;
    public Criteria Criteria { get; set; } = Criteria.SecondFloor;
    public Status Status { get; set; } = Status.None;
    public bool IsStarted { get; set; }
    public double ReferralBalance { get; set; }
    public double ReferralPercent { get; set; } = 25;
    public long? ReferrerId { get; set; }
    public double ModelPercentMin { get; set; }
    public double ModelPercentMax { get; set; } = 100;
    public virtual PromoCode? PromoCode { get; set; }
}