using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.Caching;
using System.Threading.Channels;
using Moahk.Data.Enums;
using Moahk.Other;
using Moahk.Parser.ResponseModels;
using NLog;
using Action = Moahk.Parser.ResponseModels.Action;

namespace Moahk.Parser;

public class SecondFloorGift
{
    public required TelegramGiftInfo TelegramGiftInfo { get; init; }
    public required double Price { get; init; }
    public required string BotUrl { get; init; }
}

public class ActivityLastSell
{
    public required double Price { get; init; }
    public required DateTimeOffset Time { get; init; }
}

public abstract class GiftBase
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required string Backdrop { get; init; }
    public required Activity Activity { get; init; }
    public required double Price { get; init; }
    public required string TelegramGiftId { get; init; }
    public required string BotUrl { get; init; }
    public required TelegramGiftInfo TelegramGiftInfo { get; init; }
    public SecondFloorGift? SecondFloorGift { get; init; }
    public double? Percentile25 { get; set; }
    public double? Percentile75 { get; set; }
    public double? ActivityMaxPrice { get; set; }
    public ActivityLastSell? ActivityLastSell { get; set; }
}

public class TonnelGift : GiftBase
{
    public required TonnelRelayerHistoryGiftInfoResponse[]? ActivityHistory { get; init; }
    public required string SiteUrl { get; init; }
}

public class PortalsGift : GiftBase
{
    public required Action[]? ActivityHistory { get; init; }
}

public class Gift
{
    public SignalType? Type { get; set; }
    public double? PercentDiff { get; set; }
    public double? PercentDiffWithCommission { get; set; }
    public TonnelGift? TonnelGift { get; init; }
    public PortalsGift? PortalsGift { get; init; }
}

public enum SignalType
{
    TonnelTonnel,
    TonnelPortals,
    PortalsPortals,
    PortalsTonnel
}

internal record GiftQueueItem
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required double ModelPercent { get; init; }
    public required string Backdrop { get; init; }
    public required double BackdropPercent { get; init; }
    public required string CacheKey { get; init; }
}

public class Parser : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MemoryCache _cache = new("TonnelRelayerParserCache");
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly Channel<GiftQueueItem> _giftQueue =
        Channel.CreateBounded<GiftQueueItem>(1000);

    private readonly PortalsHttpClientPool _portalsHttpClientPool = new();


    private readonly TelegramBot _telegramBot = new();
    private readonly TonnelRelayerBrowserContextPool _tonnelRelayerBrowserContextPool = new();

    public async ValueTask DisposeAsync()
    {
        await _tonnelRelayerBrowserContextPool.DisposeAsync();
        await _cancellationTokenSource.CancelAsync();
        _cancellationTokenSource.Dispose();
        _telegramBot.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task Start()
    {
        await _tonnelRelayerBrowserContextPool.Start();
        _ = RunTonnelGetMarketGiftsLoop();
        var activityThreads = new List<Task>();
        for (var i = 0; i < _tonnelRelayerBrowserContextPool.Size - 1; i++)
        {
            var activityThread = TonnelActivityThread();
            activityThreads.Add(activityThread);
        }

        _ = Task.WhenAll(activityThreads);
    }

    private async Task<TonnelGift?> GetTonnelGift(GiftQueueItem giftQueueItem, bool searchBackdrop)
    {
        var searchGifts = await TonnelSearchGift(giftQueueItem.Name, giftQueueItem.Model, giftQueueItem.ModelPercent,
            giftQueueItem.Backdrop, giftQueueItem.BackdropPercent, searchBackdrop);
        if (searchGifts is null)
        {
            Logger.Warn($"Ошибка при получении подарка {giftQueueItem.CacheKey} на тоннеле");
            return null;
        }

        if (searchGifts.Length == 0)
        {
            Logger.Warn($"Подарок {giftQueueItem.CacheKey} не найден на тоннеле");
            return null;
        }

        var activityHistory = await GetTonnelActivity(giftQueueItem.Name, giftQueueItem.Model,
            giftQueueItem.ModelPercent, giftQueueItem.Backdrop, giftQueueItem.BackdropPercent, searchBackdrop);
        var threeDaysActivity = activityHistory?
            .Where(x => x.Timestamp.HasValue && x.Timestamp.Value >= DateTimeOffset.UtcNow.AddDays(-3) &&
                        x is { GiftId: > 0, Type: "INTERNAL_SALE" })
            .ToArray();
        var activity = GetActivityFromHistory(threeDaysActivity);
        var minPriceGift = searchGifts.MinBy(x => x.Price);
        var price = CalculateTonnelPriceWithCommission((double)minPriceGift!.Price!);
        var telegramGiftId = string.Concat(minPriceGift.Name?.Where(char.IsLetter) ?? string.Empty)
                             + '-' + minPriceGift.GiftNum;
        var telegramGiftInfo = await TelegramGiftManager.GetGiftInfoAsync(telegramGiftId);
        SecondFloorGift? secondFloorGift = null;
        if (searchGifts.Length >= 2)
        {
            var secondFloorPrice = CalculateTonnelPriceWithCommission((double)searchGifts[1].Price!);
            var secondFloorTelegramGiftId = string.Concat(searchGifts[1].Name?.Where(char.IsLetter) ?? string.Empty)
                                            + '-' + searchGifts[1].GiftNum;
            var secondFloorTelegramGiftInfo = await TelegramGiftManager.GetGiftInfoAsync(secondFloorTelegramGiftId);
            secondFloorGift = new SecondFloorGift
            {
                TelegramGiftInfo = secondFloorTelegramGiftInfo,
                Price = secondFloorPrice,
                BotUrl = $"https://t.me/tonnel_network_bot/gift?startapp={searchGifts[1].GiftId}"
            };
        }

        return new TonnelGift
        {
            Name = minPriceGift.Name!,
            Model = minPriceGift.Model!,
            Backdrop = minPriceGift.Backdrop!,
            ActivityHistory = threeDaysActivity,
            Activity = activity,
            Price = price,
            TelegramGiftId = telegramGiftId,
            BotUrl = $"https://t.me/tonnel_network_bot/gift?startapp={minPriceGift.GiftId}",
            SiteUrl = $"https://market.tonnel.network/?giftDrawerId={minPriceGift.GiftId}",
            TelegramGiftInfo = telegramGiftInfo,
            SecondFloorGift = secondFloorGift
        };
    }

    private async Task<PortalsGift?> GetPortalsGift(GiftQueueItem giftQueueItem, bool searchBackdrop)
    {
        var searchGifts = await PortalsSearchGift(giftQueueItem.Name, giftQueueItem.Model, giftQueueItem.Backdrop,
            searchBackdrop);
        if (searchGifts?.Results is null)
        {
            Logger.Warn($"Ошибка при получении подарка {giftQueueItem.CacheKey} на порталах");
            return null;
        }

        if (searchGifts.Results.Length == 0)
        {
            Logger.Warn($"Подарок {giftQueueItem.CacheKey} не найден на порталах");
            return null;
        }

        List<Action> activityHistory = [];
        var page = 0;
        do
        {
            var activityHistoryResponse = await GetPortalsActivity(giftQueueItem.Name, giftQueueItem.Model,
                giftQueueItem.Backdrop, searchBackdrop, page);
            if (activityHistoryResponse?.Actions is null || activityHistoryResponse.Actions.Length == 0)
                break;
            activityHistory.AddRange(activityHistoryResponse.Actions);
            page++;
        }
        // TODO: проверить ни дохуя ли элементов
        while (activityHistory.Count % 20 == 0 || !activityHistory.Any(x =>
                   x.CreatedAt.HasValue && x.CreatedAt.Value <= DateTimeOffset.UtcNow.AddDays(-3) &&
                   x.Type == "purchase"));

        var threeDaysActivity = activityHistory.Where(x =>
                x.CreatedAt.HasValue && x.CreatedAt.Value >= DateTimeOffset.UtcNow.AddDays(-3) && x.Type == "purchase")
            .ToArray();
        var activity = GetActivityFromHistory(threeDaysActivity);
        var minPriceGift = searchGifts.Results[0];
        var price = double.Parse(minPriceGift.Price!, CultureInfo.InvariantCulture);
        var telegramGiftId = string.Concat(minPriceGift?.Name?.Where(char.IsLetter) ?? string.Empty)
                             + '-' + minPriceGift!.ExternalCollectionNumber;
        var telegramGiftInfo = await TelegramGiftManager.GetGiftInfoAsync(telegramGiftId);
        SecondFloorGift? secondFloorGift = null;
        if (searchGifts.Results.Length >= 2)
        {
            var secondFloorPrice = double.Parse(searchGifts.Results[1].Price!, CultureInfo.InvariantCulture);
            var secondFloorTelegramGiftId =
                string.Concat(searchGifts.Results[1].Name?.Where(char.IsLetter) ?? string.Empty)
                + '-' + searchGifts.Results[1].ExternalCollectionNumber;
            var secondFloorTelegramGiftInfo = await TelegramGiftManager.GetGiftInfoAsync(secondFloorTelegramGiftId);
            secondFloorGift = new SecondFloorGift
            {
                TelegramGiftInfo = secondFloorTelegramGiftInfo,
                Price = secondFloorPrice,
                BotUrl = $"https://t.me/portals/market?startapp=gift_{searchGifts.Results[1].Id}"
            };
        }

        return new PortalsGift
        {
            Name = minPriceGift.Name!,
            Model = minPriceGift.Attributes!.First(x => x.Type == "model").Value!,
            Backdrop = minPriceGift.Attributes!.First(x => x.Type == "backdrop").Value!,
            ActivityHistory = threeDaysActivity.ToArray(),
            Activity = activity,
            Price = price,
            TelegramGiftId = telegramGiftId,
            BotUrl = $"https://t.me/portals/market?startapp=gift_{minPriceGift.Id}",
            TelegramGiftInfo = telegramGiftInfo,
            SecondFloorGift = secondFloorGift
        };
    }

    private Activity GetActivityFromHistory<T>(T? activityHistory) where T : IEnumerable<object>?
    {
        return activityHistory?.Count() switch
        {
            >= 10 => Activity.High,
            >= 5 => Activity.Medium,
            _ => Activity.Low
        };
    }

    private async Task TonnelActivityThread()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            var giftQueueItem = await _giftQueue.Reader.ReadAsync();
            try
            {
                if (_cache.Contains(giftQueueItem.CacheKey))
                {
                    Logger.Info($"Подарок {giftQueueItem.CacheKey} недавно был обработан, пропускаем.");
                    continue;
                }

                _cache.Set(giftQueueItem.CacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));

                #region С фоном

                var tonnelGift = await GetTonnelGift(giftQueueItem, true);
                var portalsGift = await GetPortalsGift(giftQueueItem, true);
                var gift = new Gift
                {
                    TonnelGift = tonnelGift,
                    PortalsGift = portalsGift
                };
                await MathSecondFloor(gift, Criteria.SecondFloor);

                #endregion

                #region Без фона

                tonnelGift = await GetTonnelGift(giftQueueItem, false);
                portalsGift = await GetPortalsGift(giftQueueItem, false);
                gift = new Gift
                {
                    TonnelGift = tonnelGift,
                    PortalsGift = portalsGift
                };
                await MathSecondFloor(gift, Criteria.SecondFloorWithoutBackdrop);

                #endregion
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Ошибка при обработке подарка {giftQueueItem.CacheKey}");
            }
        }
    }

    private async Task<TonnelRelayerHistoryGiftInfoResponse[]?> GetTonnelActivity(string name, string model,
        double modelPercent, string backdrop, double backdropPercent, bool searchBackdrop)
    {
        try
        {
            TonnelRelayerHistoryGiftInfoResponse[]? response;
            if (searchBackdrop)
                response = await _tonnelRelayerBrowserContextPool
                    .PostAsJsonAsync<TonnelRelayerHistoryGiftInfoResponse[], object>(
                        "https://gifts3.tonnel.network/api/saleHistory",
                        new
                        {
                            authData = TelegramRepository.TonnelRelayerDecodedTgWebAppData,
                            page = 1,
                            limit = 50,
                            type = "ALL",
                            filter = new
                            {
                                gift_name = name,
                                model = $"{model} ({modelPercent.ToString(CultureInfo.InvariantCulture)}%)",
                                backdrop = $"{backdrop} ({backdropPercent.ToString(CultureInfo.InvariantCulture)}%)"
                            },
                            sort = new { timestamp = -1, gift_id = -1 }
                        });
            else
                response = await _tonnelRelayerBrowserContextPool
                    .PostAsJsonAsync<TonnelRelayerHistoryGiftInfoResponse[], object>(
                        "https://gifts3.tonnel.network/api/saleHistory",
                        new
                        {
                            authData = TelegramRepository.TonnelRelayerDecodedTgWebAppData,
                            page = 1,
                            limit = 50,
                            type = "ALL",
                            filter = new
                            {
                                gift_name = name,
                                model = $"{model} ({modelPercent.ToString(CultureInfo.InvariantCulture)}%)"
                            },
                            sort = new { timestamp = -1, gift_id = -1 }
                        });

            return response;
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private async Task<TonnelSearchResponse[]?> TonnelSearchGift(string name, string model, double modelPercent,
        string backdrop, double backdropPercent, bool searchBackdrop)
    {
        try
        {
            var response =
                await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelSearchResponse[], object>(
                    "https://gifts3.tonnel.network/api/pageGifts", new
                    {
                        page = 1,
                        limit = 30,
                        sort = "{\"price\":1,\"gift_id\":-1}",
                        filter =
                            "{\"price\":{\"$exists\":true},\"buyer\":{\"$exists\":false},\"gift_name\":\"" +
                            name + "\",\"model\":\"" +
                            $"{model} ({modelPercent.ToString(CultureInfo.InvariantCulture)}%)" + "\"," +
                            (searchBackdrop
                                ? "\"backdrop\":{\"$in\":[\"" +
                                  $"{backdrop} ({backdropPercent.ToString(CultureInfo.InvariantCulture)}%)" + "\"]},"
                                : string.Empty) + "\"asset\":\"TON\"}",
                        @ref = 0,
                        price_range = (object?)null,
                        user_auth =
                            TelegramRepository.TonnelRelayerDecodedTgWebAppData
                    });
            return response;
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private double CalculateTonnelPriceWithCommission(double price)
    {
        return price + price * 0.06;
    }

    private async Task MathSecondFloor(
        Gift gift,
        Criteria criteria)
    {
        // tonnel-tonnel
        if (gift.TonnelGift?.SecondFloorGift is not null)
        {
            var tonnelGift = gift.TonnelGift;
            var secondFloorPrice = tonnelGift.SecondFloorGift.Price;
            var percentDiff = MathPercentDiff(tonnelGift.Price, secondFloorPrice);
            gift.Type = SignalType.TonnelTonnel;
            gift.PercentDiff = percentDiff;
        }

        // tonnel-portals
        if (gift.TonnelGift is not null && gift.PortalsGift is not null)
        {
            var tonnelGift = gift.TonnelGift;
            var portalsGift = gift.PortalsGift;
            var secondFloorPrice = portalsGift.Price;
            var percentDiff = MathPercentDiff(tonnelGift.Price, secondFloorPrice);
            var percentDiffWithCommission = MathPercentDiffWithCommission(tonnelGift.Price, secondFloorPrice);
            if (gift.PercentDiff < percentDiff)
            {
                gift.Type = SignalType.TonnelPortals;
                gift.PercentDiff = percentDiff;
                gift.PercentDiffWithCommission = percentDiffWithCommission;
            }
        }

        // portals-portals
        if (gift.PortalsGift?.SecondFloorGift is not null)
        {
            var portalsGift = gift.PortalsGift;
            var secondFloorPrice = portalsGift.SecondFloorGift.Price;
            var percentDiff = MathPercentDiff(portalsGift.Price, secondFloorPrice);
            if (gift.PercentDiff < percentDiff)
            {
                gift.Type = SignalType.PortalsPortals;
                gift.PercentDiff = percentDiff;
            }
        }

        // portals-tonnel
        if (gift.PortalsGift is not null && gift.TonnelGift is not null)
        {
            var portalsGift = gift.PortalsGift;
            var tonnelGift = gift.TonnelGift;
            var secondFloorPrice = tonnelGift.Price;
            var percentDiff = MathPercentDiff(portalsGift.Price, secondFloorPrice);
            var percentDiffWithCommission = MathPercentDiffWithCommission(portalsGift.Price, secondFloorPrice);
            if (gift.PercentDiff < percentDiff)
            {
                gift.Type = SignalType.PortalsTonnel;
                gift.PercentDiff = percentDiff;
                gift.PercentDiffWithCommission = percentDiffWithCommission;
            }
        }

        if (gift.PercentDiff < 0)
            return;

        if (gift.Type == SignalType.TonnelTonnel)
        {
            // percentile25
            try
            {
                gift.TonnelGift!.Percentile25 = gift.TonnelGift.ActivityHistory?
                    .Select(x => (double)x.Price!)
                    .Percentile(25);
            }
            catch
            {
                // ignored
            }

            // percentile75
            try
            {
                gift.TonnelGift!.Percentile75 = gift.TonnelGift.ActivityHistory?
                    .Select(x => (double)x.Price!)
                    .Percentile(75);
            }
            catch
            {
                // ignored
            }

            // activity max price
            gift.TonnelGift!.ActivityMaxPrice = gift.TonnelGift.ActivityHistory?
                .OrderByDescending(x => x.Price)
                .FirstOrDefault()?.Price;
            // activity last sell
            var lastSell = gift.TonnelGift.ActivityHistory?
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (lastSell is { Price: not null, Timestamp: not null })
                gift.TonnelGift.ActivityLastSell = new ActivityLastSell
                {
                    Price = lastSell.Price.Value,
                    Time = lastSell.Timestamp.Value
                };
        }
        else if (gift.Type is SignalType.TonnelPortals or SignalType.PortalsTonnel)
        {
            // tonnel
            // percentile25
            try
            {
                gift.TonnelGift!.Percentile25 = gift.TonnelGift.ActivityHistory?
                    .Select(x => (double)x.Price!)
                    .Percentile(25);
            }
            catch
            {
                // ignored
            }

            // percentile75
            try
            {
                gift.TonnelGift!.Percentile75 = gift.TonnelGift.ActivityHistory?
                    .Select(x => (double)x.Price!)
                    .Percentile(75);
            }
            catch
            {
                // ignored
            }

            // activity max price
            gift.TonnelGift!.ActivityMaxPrice = gift.TonnelGift.ActivityHistory?
                .OrderByDescending(x => x.Price)
                .FirstOrDefault()?.Price;
            // activity last price
            var lastSell = gift.TonnelGift.ActivityHistory?
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (lastSell is { Price: not null, Timestamp: not null })
                gift.TonnelGift.ActivityLastSell = new ActivityLastSell
                {
                    Price = lastSell.Price.Value,
                    Time = lastSell.Timestamp.Value
                };
            // portals
            var portalsActivityPrices = gift.PortalsGift?.ActivityHistory?
                .Select(x => (x, double.Parse(x.Amount!, CultureInfo.InvariantCulture))).ToArray();
            // percentile25
            try
            {
                gift.PortalsGift!.Percentile25 = portalsActivityPrices?.Select(x => x.Item2).Percentile(25);
            }
            catch
            {
                // ignored
            }

            // percentile75
            try
            {
                gift.PortalsGift!.Percentile75 = portalsActivityPrices?.Select(x => x.Item2).Percentile(75);
            }
            catch
            {
                // ignored
            }

            // activity max price
            gift.PortalsGift!.ActivityMaxPrice = portalsActivityPrices?.Select(x => x.Item2).Max();
            // activity last price
            gift.PortalsGift.ActivityLastSell = portalsActivityPrices?[0] != null
                ? new ActivityLastSell
                {
                    Price = portalsActivityPrices[0].Item2,
                    Time = portalsActivityPrices[0].Item1.CreatedAt!.Value
                }
                : null;
        }
        else if (gift.Type == SignalType.TonnelTonnel)
        {
            var portalsActivityPrices = gift.PortalsGift?.ActivityHistory?
                .Select(x => (x, double.Parse(x.Amount!, CultureInfo.InvariantCulture))).ToArray();
            // percentile25
            try
            {
                gift.PortalsGift!.Percentile25 = portalsActivityPrices?.Select(x => x.Item2).Percentile(25);
            }
            catch
            {
                // ignored
            }

            // percentile75
            try
            {
                gift.PortalsGift!.Percentile75 = portalsActivityPrices?.Select(x => x.Item2).Percentile(75);
            }
            catch
            {
                // ignored
            }

            // activity max price
            gift.PortalsGift!.ActivityMaxPrice = portalsActivityPrices?.Select(x => x.Item2).Max();
            // activity last price
            gift.PortalsGift.ActivityLastSell = portalsActivityPrices?[0] != null
                ? new ActivityLastSell
                {
                    Price = portalsActivityPrices[0].Item2,
                    Time = portalsActivityPrices[0].Item1.CreatedAt!.Value
                }
                : null;
        }

        await _telegramBot.SendSignal(gift, criteria);
    }

    private double MathPercentDiff(double firstFloorPrice, double secondFloorPrice)
    {
        return (secondFloorPrice - firstFloorPrice) / secondFloorPrice * 100.0;
    }

    private double MathPercentDiffWithCommission(double firstFloorPrice, double secondFloorPrice)
    {
        const double fixedTonCommission = 0.36;
        return (secondFloorPrice - firstFloorPrice - fixedTonCommission) / secondFloorPrice * 100.0;
    }

    private async Task<PortalsSearchResponse?> PortalsSearchGift(string collection, string model, string backdrop,
        bool searchBackdrop)
    {
        try
        {
            var url = "https://portals-market.com/api/nfts/search?offset=0&limit=20" +
                      (searchBackdrop
                          ? $"&filter_by_backdrops={backdrop.Replace(' ', '+')}"
                          : string.Empty) +
                      $"&filter_by_collections={collection.Replace(' ', '+')}" +
                      $"&filter_by_models={model.Replace(' ', '+')}" +
                      "&sort_by=price+asc" +
                      "&status=listed";
            using var response = await _portalsHttpClientPool.SendAsync(url, HttpMethod.Get);
            if (!response.IsSuccessStatusCode) return null;

            var responseData = await response.Content.ReadFromJsonAsync<PortalsSearchResponse>();
            return responseData;
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private async Task<PortalsActionsResponse?> GetPortalsActivity(string name, string model, string backdrop,
        bool searchBackdrop, int page)
    {
        // https://portals-market.com/api/market/actions/?offset=0&limit=20&filter_by_backdrops=Ranger+Green&filter_by_collections=Big+Year&filter_by_models=Jelly+Year&sort_by=listed_at+desc
        var url = $"https://portals-market.com/api/market/actions/?offset={page * 20}&limit=20" +
                  (searchBackdrop
                      ? $"&filter_by_backdrops={backdrop.Replace(' ', '+')}"
                      : string.Empty) +
                  $"&filter_by_collections={name.Replace(' ', '+')}" +
                  $"&filter_by_models={model.Replace(' ', '+')}" +
                  "&sort_by=listed_at+desc";
        try
        {
            using var response = await _portalsHttpClientPool.SendAsync(url, HttpMethod.Get);
            if (!response.IsSuccessStatusCode) return null;

            var responseData = await response.Content.ReadFromJsonAsync<PortalsActionsResponse>();
            if (responseData?.Actions != null && responseData.Actions.Length != 0)
                return responseData;
            return null;
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private async Task RunTonnelGetMarketGiftsLoop()
    {
        Logger.Info("Запущен цикл GetMarketGifts");
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                await TonnelGetMarketGifts();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Ошибка при выполнении GetMarketGifts");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), _cancellationTokenSource.Token);
        }
    }

    private async Task TonnelGetMarketGifts()
    {
        await TonnelGetMarketGiftPage(1);
    }

    private async Task TonnelGetMarketGiftPage(int page)
    {
        try
        {
            var response =
                await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelRelayerGiftInfoResponse[], object>(
                    "https://gifts3.tonnel.network/api/pageGifts", new
                    {
                        page,
                        limit = 30,
                        sort = "{\"message_post_time\":-1,\"gift_id\":-1}",
                        filter = "{\"price\":{\"$exists\":true},\"buyer\":{\"$exists\":false},\"asset\":\"TON\"}",
                        @ref = 0,
                        price_range = (object?)null,
                        user_auth = TelegramRepository.TonnelRelayerDecodedTgWebAppData
                    });
            if (response == null || response.Length == 0) throw new Exception("Не удалось получить данные о подарках.");
            foreach (var tonnelRelayerGiftInfo in response)
            {
                if (tonnelRelayerGiftInfo.GiftId < 0)
                    continue;
                try
                {
                    var cacheKey =
                        $"tonnelRelayerGiftInfo_{tonnelRelayerGiftInfo.GiftId}_{tonnelRelayerGiftInfo.Price}";
                    var name = tonnelRelayerGiftInfo.Name?.Trim();
                    var modelRaw = tonnelRelayerGiftInfo.Model;
                    var backdropRaw = tonnelRelayerGiftInfo.Backdrop;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(modelRaw) ||
                        string.IsNullOrWhiteSpace(backdropRaw))
                        continue;
                    var modelSpaceIdx = modelRaw.LastIndexOf(' ');
                    var backdropSpaceIdx = backdropRaw.LastIndexOf(' ');
                    if (modelSpaceIdx < 0 || backdropSpaceIdx < 0)
                        continue;
                    var model = modelRaw[..modelSpaceIdx].Trim();
                    var backdrop = backdropRaw[..backdropSpaceIdx].Trim();
                    var modelPercentStr =
                        new string(modelRaw[modelSpaceIdx..].Where(x => char.IsDigit(x) || x is '.' or ',').ToArray())
                            .Replace(',', '.');
                    var backdropPercentStr =
                        new string(backdropRaw[backdropSpaceIdx..].Where(x => char.IsDigit(x) || x is '.' or ',')
                            .ToArray()).Replace(',', '.');
                    if (!double.TryParse(modelPercentStr, CultureInfo.InvariantCulture, out var modelPercent))
                        continue;
                    if (!double.TryParse(backdropPercentStr, CultureInfo.InvariantCulture, out var backdropPercent))
                        continue;
                    await _giftQueue.Writer.WriteAsync(
                        new GiftQueueItem
                        {
                            Name = name,
                            Model = model,
                            ModelPercent = modelPercent,
                            Backdrop = backdrop,
                            BackdropPercent = backdropPercent,
                            CacheKey = cacheKey
                        }, _cancellationTokenSource.Token);
                }
                catch (Exception e)
                {
                    Logger.Warn(
                        $"Ошибка при получении информации о подарке {tonnelRelayerGiftInfo.GiftId}: {e.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Ошибка при получении страницы подарков: {ex.Message}");
        }
    }
}