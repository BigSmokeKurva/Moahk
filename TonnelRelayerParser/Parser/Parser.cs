using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.Caching;
using System.Threading.Channels;
using Moahk.Data.Enums;
using Moahk.Other;
using Moahk.Parser.ResponseModels;
using NLog;

namespace Moahk.Parser;

public class SecondFloorGift
{
    public required TelegramGiftInfo TelegramGiftInfo { get; init; }
    public GiftBubblesDataGift? GiftBubblesDataGift { get; init; }
    public required double Price { get; init; }
    public required string BotUrl { get; init; }
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
    public Action[]? ActivityHistory3Days { get; init; }
    public Action[]? ActivityHistoryAll { get; init; }
    public GiftBubblesDataGift? BubblesDataGift { get; init; }
}

public class TonnelGift : GiftBase
{
    public required string SiteUrl { get; init; }
}

public class PortalsGift : GiftBase
{
}

public class Action
{
    public required DateTimeOffset CreatedAt { get; init; }
    public required double Price { get; init; }
}

public class Gift
{
    public SignalType? Type { get; set; }
    public double PercentDiff { get; set; }
    public double? PercentDiffWithCommission { get; set; }
    public TonnelGift? TonnelGift { get; init; }
    public PortalsGift? PortalsGift { get; init; }
}

public enum Market
{
    Tonnel,
    Portals
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

        var activityHistorySale = await GetTonnelActivity(giftQueueItem.Name, giftQueueItem.Model,
            giftQueueItem.ModelPercent, giftQueueItem.Backdrop, giftQueueItem.BackdropPercent, searchBackdrop, "SALE");
        var activityHistoryInternalSale = await GetTonnelActivity(giftQueueItem.Name, giftQueueItem.Model,
            giftQueueItem.ModelPercent, giftQueueItem.Backdrop, giftQueueItem.BackdropPercent, searchBackdrop,
            "INTERNAL_SALE");
        var activityHistory = activityHistorySale?
            .Concat(activityHistoryInternalSale ?? [])
            .GroupBy(x => new { x.Timestamp, x.Price })
            .Select(x => x.First())
            .OrderByDescending(x => x.Timestamp)
            .ToArray();
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
                BotUrl = $"https://t.me/tonnel_network_bot/gift?startapp={searchGifts[1].GiftId}",
                GiftBubblesDataGift = GiftBubbleRepository.GetGiftData(secondFloorTelegramGiftInfo.Collection)
            };
        }

        var activityHistoryAll = activityHistory?
            .Select(x => new Action
            {
                CreatedAt = (DateTimeOffset)x.Timestamp!,
                Price = (double)x.Price!
            }).ToArray();
        var actionHistory3days = activityHistoryAll?
            .Where(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-3))
            .ToArray();
        var activity = GetActivityFromHistory(actionHistory3days);
        return new TonnelGift
        {
            Name = minPriceGift.Name!,
            Model = minPriceGift.Model!,
            Backdrop = minPriceGift.Backdrop!,
            ActivityHistoryAll = activityHistoryAll,
            ActivityHistory3Days = actionHistory3days,
            Activity = activity,
            Price = price,
            TelegramGiftId = telegramGiftId,
            BotUrl = $"https://t.me/tonnel_network_bot/gift?startapp={minPriceGift.GiftId}",
            SiteUrl = $"https://market.tonnel.network/?giftDrawerId={minPriceGift.GiftId}",
            TelegramGiftInfo = telegramGiftInfo,
            SecondFloorGift = secondFloorGift,
            BubblesDataGift = GiftBubbleRepository.GetGiftData(telegramGiftInfo.Collection)
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

        var activityHistory = await GetPortalsActivity(giftQueueItem.Name, giftQueueItem.Model,
            giftQueueItem.Backdrop, searchBackdrop);

        var minPriceGift = searchGifts.Results[0];
        var price = double.Parse(minPriceGift.Price!, CultureInfo.InvariantCulture);
        var telegramGiftId = string.Concat(minPriceGift.Name?.Where(char.IsLetter) ?? string.Empty)
                             + '-' + minPriceGift.ExternalCollectionNumber;
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
                BotUrl = $"https://t.me/portals/market?startapp=gift_{searchGifts.Results[1].Id}",
                GiftBubblesDataGift = GiftBubbleRepository.GetGiftData(secondFloorTelegramGiftInfo.Collection)
            };
        }

        var activityHistoryAll = activityHistory?.Actions?
            .Select(x => new Action
            {
                CreatedAt = (DateTimeOffset)x.CreatedAt!,
                Price = double.Parse(x.Amount!, CultureInfo.InvariantCulture)
            }).ToArray();
        var actionHistory3days = activityHistoryAll?
            .Where(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-3))
            .ToArray();
        var activity = GetActivityFromHistory(actionHistory3days);
        return new PortalsGift
        {
            Name = minPriceGift.Name!,
            Model = minPriceGift.Attributes!.First(x => x.Type == "model").Value!,
            Backdrop = minPriceGift.Attributes!.First(x => x.Type == "backdrop").Value!,
            ActivityHistoryAll = activityHistoryAll,
            ActivityHistory3Days = actionHistory3days,
            Activity = activity,
            Price = price,
            TelegramGiftId = telegramGiftId,
            BotUrl = $"https://t.me/portals/market?startapp=gift_{minPriceGift.Id}",
            TelegramGiftInfo = telegramGiftInfo,
            SecondFloorGift = secondFloorGift,
            BubblesDataGift = GiftBubbleRepository.GetGiftData(telegramGiftInfo.Collection)
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
        double modelPercent, string backdrop, double backdropPercent, bool searchBackdrop, string type)
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
                            authData = TelegramAccountRepository.TonnelRelayerDecodedTgWebAppData,
                            page = 1,
                            limit = 50,
                            type,
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
                            authData = TelegramAccountRepository.TonnelRelayerDecodedTgWebAppData,
                            page = 1,
                            limit = 50,
                            type,
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
                            TelegramAccountRepository.TonnelRelayerDecodedTgWebAppData
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
        // // tonnel-tonnel
        // if (gift.TonnelGift?.SecondFloorGift is not null)
        // {
        //     var tonnelGift = gift.TonnelGift;
        //     var secondFloorPrice = tonnelGift.SecondFloorGift.Price;
        //     var percentDiff = MathPercentDiff(tonnelGift.Price, secondFloorPrice);
        //     gift.Type = SignalType.TonnelTonnel;
        //     gift.PercentDiff = percentDiff;
        // }
        //
        // // tonnel-portals
        // if (gift.TonnelGift is not null && gift.PortalsGift is not null)
        // {
        //     var tonnelGift = gift.TonnelGift;
        //     var portalsGift = gift.PortalsGift;
        //     var secondFloorPrice = portalsGift.Price;
        //     var percentDiff = MathPercentDiff(tonnelGift.Price, secondFloorPrice);
        //     var percentDiffWithCommission = MathPercentDiffWithCommission(tonnelGift.Price, secondFloorPrice);
        //     if (gift.PercentDiff < percentDiff)
        //     {
        //         gift.Type = SignalType.TonnelPortals;
        //         gift.PercentDiff = percentDiff;
        //         gift.PercentDiffWithCommission = percentDiffWithCommission;
        //     }
        // }
        //
        // // portals-portals
        // if (gift.PortalsGift?.SecondFloorGift is not null)
        // {
        //     var portalsGift = gift.PortalsGift;
        //     var secondFloorPrice = portalsGift.SecondFloorGift.Price;
        //     var percentDiff = MathPercentDiff(portalsGift.Price, secondFloorPrice);
        //     if (gift.PercentDiff < percentDiff)
        //     {
        //         gift.Type = SignalType.PortalsPortals;
        //         gift.PercentDiff = percentDiff;
        //     }
        // }
        //
        // // portals-tonnel
        // if (gift.PortalsGift is not null && gift.TonnelGift is not null)
        // {
        //     var portalsGift = gift.PortalsGift;
        //     var tonnelGift = gift.TonnelGift;
        //     var secondFloorPrice = tonnelGift.Price;
        //     var percentDiff = MathPercentDiff(portalsGift.Price, secondFloorPrice);
        //     var percentDiffWithCommission = MathPercentDiffWithCommission(portalsGift.Price, secondFloorPrice);
        //     if (gift.PercentDiff < percentDiff)
        //     {
        //         gift.Type = SignalType.PortalsTonnel;
        //         gift.PercentDiff = percentDiff;
        //         gift.PercentDiffWithCommission = percentDiffWithCommission;
        //     }
        // }

        // first sloor: ищем минимальную цену
        (Market market, double? price)[] firstFloorOptions =
        [
            (Market.Tonnel, gift.TonnelGift?.Price),
            (Market.Portals, gift.PortalsGift?.Price)
        ];
        var (firstFloorMarket, firstFloorPrice) = firstFloorOptions.OrderBy(x => x.price).FirstOrDefault();
        if (firstFloorPrice is null)
            return;

        // second floor: ищем максимальную цену среди возможных вариантов
        (Market market, double? price, SignalType type)[] secondFloorOptions =
        [
            (Market.Tonnel, gift.TonnelGift?.SecondFloorGift?.Price, SignalType.TonnelTonnel),
            (Market.Portals, gift.PortalsGift?.Price,
                SignalType.TonnelPortals),
            (Market.Portals, gift.PortalsGift?.SecondFloorGift?.Price, SignalType.PortalsPortals),
            (Market.Tonnel, gift.TonnelGift?.Price,
                SignalType.PortalsTonnel)
        ];

        var validSecondFloorOptions = secondFloorOptions
            .Where(x => x.price is not null)
            .Where(x =>
                (firstFloorMarket == Market.Tonnel &&
                 x.type is SignalType.TonnelTonnel or SignalType.TonnelPortals) ||
                (firstFloorMarket == Market.Portals &&
                 x.type is SignalType.PortalsPortals or SignalType.PortalsTonnel)
            ).ToArray();

        if (validSecondFloorOptions.Length == 0)
            return;

        var (_, secondFloorPrice, signalType) = validSecondFloorOptions
            .OrderByDescending(x => x.price)
            .FirstOrDefault();
        if (secondFloorPrice is null)
            return;

        gift.Type = signalType;
        if (gift.Type is null)
            return;
        gift.PercentDiff = MathPercentDiff(firstFloorPrice.Value, secondFloorPrice.Value);
        if (gift.PercentDiff < 0)
            return;
        switch (gift.Type)
        {
            case SignalType.TonnelTonnel:
                TrySetPercentiles(gift.TonnelGift);
                break;
            case SignalType.TonnelPortals or SignalType.PortalsTonnel:
                TrySetPercentiles(gift.TonnelGift);
                TrySetPercentiles(gift.PortalsGift);
                gift.PercentDiffWithCommission =
                    MathPercentDiffWithCommission(firstFloorPrice.Value, secondFloorPrice.Value);
                break;
            case SignalType.PortalsPortals:
                TrySetPercentiles(gift.PortalsGift);
                break;
        }

        await _telegramBot.SendSignal(gift, criteria);
    }

    private void TrySetPercentiles(GiftBase? gift)
    {
        if (gift == null) return;
        try
        {
            gift.Percentile25 = gift.ActivityHistory3Days?.Select(x => x.Price).Percentile(25);
        }
        catch
        {
            // ignored
        }

        try
        {
            gift.Percentile75 = gift.ActivityHistory3Days?.Select(x => x.Price).Percentile(75);
        }
        catch
        {
            // ignored
        }
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
        bool searchBackdrop)
    {
        var url = "https://portals-market.com/api/market/actions/?offset=0&limit=20" +
                  (searchBackdrop
                      ? $"&filter_by_backdrops={backdrop.Replace(' ', '+')}"
                      : string.Empty) +
                  $"&filter_by_collections={name.Replace(' ', '+')}" +
                  $"&filter_by_models={model.Replace(' ', '+')}" +
                  "&sort_by=listed_at+desc&action_types=buy";
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
                await GetMarketGifts();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Ошибка при выполнении GetMarketGifts");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), _cancellationTokenSource.Token);
        }
    }

    private async Task GetMarketGifts()
    {
        await Task.WhenAll(
            TonnelGetMarketGiftPage(1),
            PortalsGetMarketGiftPage(1)
        );
    }

    private async Task PortalsGetMarketGiftPage(int page)
    {
        try
        {
            using var response = await _portalsHttpClientPool.SendAsync(
                $"https://portals-market.com/api/nfts/search?offset={(page - 1) * 50}&limit=50&sort_by=listed_at+desc&status=listed",
                HttpMethod.Get);
            if (!response.IsSuccessStatusCode)
                throw new Exception(
                    $"Не удалось получить страницу подарков на порталах. Статус код: {response.StatusCode}");
            var responseData = await response.Content.ReadFromJsonAsync<PortalsSearchResponse>();
            if (responseData?.Results == null || responseData.Results.Length == 0)
                throw new Exception("Не удалось получить данные о подарках на порталах.");
            foreach (var portalsSearchResult in responseData.Results)
            {
                if (portalsSearchResult.ExternalCollectionNumber < 0)
                    continue;
                try
                {
                    var cacheKey = $"portalsGiftInfo_{portalsSearchResult.Id}_{portalsSearchResult.Price}";
                    var name = portalsSearchResult.Name?.Trim();
                    var modelAttribute = portalsSearchResult.Attributes?.FirstOrDefault(x => x.Type == "model");
                    var model = modelAttribute?.Value?.Trim();
                    var modelPercent = modelAttribute!.RarityPerMille;
                    var backdropAttribute = portalsSearchResult.Attributes?.FirstOrDefault(x => x.Type == "backdrop");
                    var backdrop = backdropAttribute?.Value?.Trim();
                    var backdropPercent = backdropAttribute?.RarityPerMille;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(model) ||
                        string.IsNullOrWhiteSpace(backdrop))
                        continue;
                    await _giftQueue.Writer.WriteAsync(
                        new GiftQueueItem
                        {
                            Name = name,
                            Model = model,
                            ModelPercent = (double)modelPercent!,
                            Backdrop = backdrop,
                            BackdropPercent = (double)backdropPercent!,
                            CacheKey = cacheKey
                        }, _cancellationTokenSource.Token);
                }
                catch (Exception e)
                {
                    Logger.Warn(
                        $"Ошибка при добавлении в очередь подарка {portalsSearchResult.Id}: {e.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Ошибка при получении страницы подарков: {ex.Message}");
        }
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
                        user_auth = TelegramAccountRepository.TonnelRelayerDecodedTgWebAppData
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
                        $"Ошибка при добавлении в очередь подарка {tonnelRelayerGiftInfo.GiftId}: {e.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Ошибка при получении страницы подарков: {ex.Message}");
        }
    }
}