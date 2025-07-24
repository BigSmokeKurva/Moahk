using Moahk.Data.Enums;

namespace Moahk.Data.Entities;

public class User
{
    public long Id { get; init; }
    public DateTimeOffset? License { get; set; }
    public double ReferralBalance { get; set; }
    public double ReferralPercent { get; set; } = 25;
    public long? ReferrerId { get; set; }
    public virtual PromoCode? PromoCode { get; set; }

    #region Config

    public double PriceMin { get; set; }
    public double PriceMax { get; set; } = 10000;
    public int ProfitPercent { get; set; } = 10;
    public Criteria Criteria { get; set; } = Criteria.SecondFloor;
    public Status Status { get; set; } = Status.None;
    public bool IsStarted { get; set; }
    public double ModelPercentMin { get; set; }
    public double ModelPercentMax { get; set; } = 100;
    public List<SignalType> SignalTypes { get; set; } = Enum.GetValues<SignalType>().ToList();
    public List<Activity> Activities { get; set; } = Enum.GetValues<Activity>().ToList();
    public List<GiftSaleStatus> GiftSaleStatuses { get; set; } = Enum.GetValues<GiftSaleStatus>().ToList();
    public MessageType MessageType { get; set; } = MessageType.Full;

    #endregion
}