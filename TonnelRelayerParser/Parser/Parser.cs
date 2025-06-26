using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.Caching;
using System.Threading.Channels;
using Moahk.Data.Enums;
using Moahk.Other;
using Moahk.Parser.ResponseModels;
using NLog;

namespace Moahk.Parser;

public class Parser : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MemoryCache _cache = new("TonnelRelayerParserCache");
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly Channel<(GiftInfo, TonnelRelayerGiftInfo)> _giftInfos =
        Channel.CreateBounded<(GiftInfo, TonnelRelayerGiftInfo)>(1000);

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

    private async Task TonnelActivityThread()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            var giftInfo = await _giftInfos.Reader.ReadAsync();
            var cacheKey = giftInfo.Item1.Id + giftInfo.Item2.Price;
            try
            {
                if (giftInfo.Item2.GiftId < 0)
                {
                    Logger.Warn($"Подарок {giftInfo.Item1.Id} имеет id < 0, пропускаем.");
                    continue;
                }

                if (_cache.Contains(cacheKey))
                {
                    Logger.Info($"Подарок {giftInfo.Item1.Id} недавно был обработан, пропускаем.");
                    continue;
                }

                var historyData = await GetTonnelActivity(giftInfo);
                if (historyData == null || historyData.Length == 0) continue;
                var portalsGift = await PortalsCheckGift(giftInfo);
                _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));


                var lastTwoWeeksItems = historyData
                    .Where(x => x.Timestamp.HasValue && x.Timestamp.Value >= DateTimeOffset.UtcNow.AddDays(-14) &&
                                x.GiftId > 0)
                    .ToArray();
                var activity = lastTwoWeeksItems.Length switch
                {
                    < 5 => Activity.Low,
                    < 10 => Activity.Medium,
                    _ => Activity.High
                };
                var tonnelCurrentPrice = giftInfo.Item2.Price + giftInfo.Item2.Price * 0.04;
                double? portalsCurrentPrice = portalsGift?.Price != null
                    ? double.Parse(portalsGift.Price, CultureInfo.InvariantCulture)
                    : null;
                if (tonnelCurrentPrice is null && portalsCurrentPrice is null)
                {
                    Logger.Warn($"Подарок {giftInfo.Item1.Id} не имеет цены на порталах и тоннеле.");
                    continue;
                }

                (string Name, string Model, string Backdrop, double Price, double GiftId, Activity Activity, string
                    TgUrl, string BotUrl, string? SiteUrl, string botName, bool isSold, double? alternativePrice, DateTimeOffset lastActivity, double lastActivityPrice) gift =
                        portalsCurrentPrice is null || portalsCurrentPrice > tonnelCurrentPrice
                            ? (giftInfo.Item2.Name!, giftInfo.Item2.Model!, giftInfo.Item2.Backdrop!,
                                tonnelCurrentPrice ?? throw new Exception("Tonnel current price is null."),
                                giftInfo.Item2.GiftId,
                                activity,
                                $"https://t.me/nft/{giftInfo.Item1.Id}",
                                $"https://t.me/tonnel_network_bot/gift?startapp={giftInfo.Item2.GiftId}",
                                $"https://market.tonnel.network/?giftDrawerId={giftInfo.Item2.GiftId}", "tonnel",
                                giftInfo.Item1.IsSold, portalsCurrentPrice, historyData[0].Timestamp!.Value, historyData[0].Price!.Value)
                            : (portalsGift!.Name!, giftInfo.Item2.Model!, giftInfo.Item2.Backdrop!,
                                portalsCurrentPrice ?? throw new Exception("Portals current price is null."),
                                giftInfo.Item2.GiftId, activity,
                                $"https://t.me/nft/{portalsGift.TgId}",
                                $"https://t.me/portals/market?startapp=gift_{portalsGift.Id}",
                                null, "portals", giftInfo.Item1.IsSold, tonnelCurrentPrice, historyData[0].Timestamp!.Value, historyData[0].Price!.Value);


                await MathPeak(gift, lastTwoWeeksItems);
                await MathPercentile75(gift, lastTwoWeeksItems);
                var tonnelSearchResults = await GetTonnelSearchResults(giftInfo);
                var tonnelGiftFirst = tonnelSearchResults?.MinBy(x => x.Price);
                if (tonnelGiftFirst == null) continue;

                tonnelCurrentPrice = tonnelGiftFirst?.Price + tonnelGiftFirst?.Price * 0.04;
                gift =
                    portalsCurrentPrice is null || portalsCurrentPrice > tonnelCurrentPrice
                        ? (giftInfo.Item2.Name!, tonnelGiftFirst!.Model!, tonnelGiftFirst.Backdrop!,
                            tonnelCurrentPrice ?? throw new Exception("Tonnel current price is null."),
                            (double)tonnelGiftFirst.GiftId!,
                            activity,
                            $"https://t.me/nft/{giftInfo.Item1.Id}",
                            $"https://t.me/tonnel_network_bot/gift?startapp={tonnelGiftFirst.GiftId}",
                            $"https://market.tonnel.network/?giftDrawerId={tonnelGiftFirst.GiftId}", "tonnel",
                            giftInfo.Item1.IsSold, portalsCurrentPrice, historyData[0].Timestamp!.Value, historyData[0].Price!.Value)
                        : (portalsGift!.Name!, giftInfo.Item2.Model!, giftInfo.Item2.Backdrop!,
                            portalsCurrentPrice ?? throw new Exception("Portals current price is null."),
                            giftInfo.Item2.GiftId, activity,
                            $"https://t.me/nft/{giftInfo.Item1.Id}",
                            $"https://t.me/portals/market?startapp=gift_{portalsGift.Id}",
                            null, "portals", giftInfo.Item1.IsSold, tonnelCurrentPrice, historyData[0].Timestamp!.Value, historyData[0].Price!.Value);
                await MathSecondFloor(gift, tonnelSearchResults!, Criteria.SecondFloorWithBackdrop);
                // backdrop false
                portalsGift = await PortalsCheckGift(giftInfo, false);
                portalsCurrentPrice = portalsGift?.Price != null
                    ? double.Parse(portalsGift.Price, CultureInfo.InvariantCulture)
                    : null;
                tonnelSearchResults = await GetTonnelSearchResults(giftInfo, false);
                tonnelGiftFirst = tonnelSearchResults?.MinBy(x => x.Price);
                if (tonnelGiftFirst == null) continue;

                tonnelCurrentPrice = tonnelGiftFirst?.Price + tonnelGiftFirst?.Price * 0.04;
                gift =
                    portalsCurrentPrice is null || portalsCurrentPrice > tonnelCurrentPrice
                        ? (giftInfo.Item2.Name!, tonnelGiftFirst!.Model!, tonnelGiftFirst.Backdrop!,
                            tonnelCurrentPrice ?? throw new Exception("Tonnel current price is null."),
                            (double)tonnelGiftFirst.GiftId!,
                            activity,
                            $"https://t.me/nft/{giftInfo.Item1.Id}",
                            $"https://t.me/tonnel_network_bot/gift?startapp={tonnelGiftFirst.GiftId}",
                            $"https://market.tonnel.network/?giftDrawerId={tonnelGiftFirst.GiftId}", "tonnel",
                            giftInfo.Item1.IsSold, portalsCurrentPrice, historyData[0].Timestamp!.Value, historyData[0].Price!.Value)
                        : (portalsGift!.Name!, giftInfo.Item2.Model!, giftInfo.Item2.Backdrop!,
                            portalsCurrentPrice ?? throw new Exception("Portals current price is null."),
                            giftInfo.Item2.GiftId, activity,
                            $"https://t.me/nft/{giftInfo.Item1.Id}",
                            $"https://t.me/portals/market?startapp=gift_{portalsGift.Id}",
                            null, "portals", giftInfo.Item1.IsSold, tonnelCurrentPrice, historyData[0].Timestamp!.Value, historyData[0].Price!.Value);
                await MathSecondFloor(gift, tonnelSearchResults!, Criteria.SecondFloor);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ошибка при обработке подарка {giftInfo.Item1.Id}: {ex.Message}");
            }
        }
    }

    private async Task<TonnelRelayerHistoryGiftInfo[]?> GetTonnelActivity((GiftInfo, TonnelRelayerGiftInfo) giftInfo)
    {
        var response = await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelRelayerHistoryGiftInfo[], object>(
            "https://gifts3.tonnel.network/api/saleHistory",
            new
            {
                authData = TelegramRepository.TonnelRelayerDecodedTgWebAppData,
                page = 1,
                limit = 50,
                type = "ALL",
                filter = new
                {
                    gift_name = giftInfo.Item2.Name,
                    model = giftInfo.Item2.Model,
                    backdrop = giftInfo.Item2.Backdrop
                },
                sort = new { timestamp = -1, gift_id = -1 }
            });

        return response;
    }

    private async Task<TonnelSearch[]?> GetTonnelSearchResults((GiftInfo, TonnelRelayerGiftInfo) giftInfo,
        bool searchBackdrop = true)
    {
        var response = await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelSearch[], object>(
            "https://gifts3.tonnel.network/api/pageGifts", new
            {
                page = 1,
                limit = 30,
                sort = "{\"price\":1,\"gift_id\":-1}",
                filter =
                    "{\"price\":{\"$exists\":true},\"buyer\":{\"$exists\":false},\"gift_name\":\"" +
                    giftInfo.Item2.Name + "\",\"model\":\"" + giftInfo.Item2.Model + "\"," + (searchBackdrop
                        ? "\"backdrop\":{\"$in\":[\"" +
                          giftInfo.Item2.Backdrop + "\"]},"
                        : string.Empty) + "\"asset\":\"TON\"}",
                @ref = 0,
                price_range = (object?)null,
                user_auth =
                    TelegramRepository.TonnelRelayerDecodedTgWebAppData
            });
        return response;
    }

    private async Task MathPeak(
        (string Name, string Model, string Backdrop, double Price, double GiftId, Activity Activity, string TgUrl,
            string BotUrl, string? SiteUrl, string botName, bool isSold, double? alternativePrice, DateTimeOffset lastActivity, double lastActivityPrice) gift,
        TonnelRelayerHistoryGiftInfo[] lastTwoWeeksItems)
    {
        if (lastTwoWeeksItems.Length == 0) return;
        var maxPrice = lastTwoWeeksItems.OrderByDescending(x => x.Price).First().Price;
        var percentDiff = (maxPrice - gift.Price) / maxPrice * 100.0;
        if (percentDiff > 0)
            await _telegramBot.SendSignal(gift.Name, gift.Model, gift.Price, (double)percentDiff, gift.isSold,
                gift.Activity, gift.TgUrl, gift.BotUrl, gift.SiteUrl, gift.botName, Criteria.Peak,
                gift.alternativePrice, gift.lastActivity, gift.lastActivityPrice, gift.Backdrop);
    }

    private async Task MathPercentile75(
        (string Name, string Model, string Backdrop, double Price, double GiftId, Activity Activity, string TgUrl,
            string BotUrl, string? SiteUrl, string botName, bool isSold, double? alternativePrice, DateTimeOffset lastActivity, double lastActivityPrice) gift,
        TonnelRelayerHistoryGiftInfo[] lastTwoWeeksItems)
    {
        var percentile = lastTwoWeeksItems.Select(x => (double)x.Price!).Percentile(75);
        var percentDiff = (percentile - gift.Price) / percentile * 100.0;
        if (percentDiff > 0)
            await _telegramBot.SendSignal(gift.Name, gift.Model, gift.Price, percentDiff, gift.isSold, gift.Activity,
                gift.TgUrl, gift.BotUrl, gift.SiteUrl, gift.botName, Criteria.Percentile75, gift.alternativePrice, gift.lastActivity, gift.lastActivityPrice, gift.Backdrop);
    }

    private async Task MathSecondFloor(
        (string Name, string Model, string Backdrop, double Price, double GiftId, Activity Activity, string TgUrl,
            string BotUrl, string? SiteUrl, string botName, bool isSold, double? alternativePrice, DateTimeOffset lastActivity, double lastActivityPrice) gift,
        TonnelSearch[] tonnelSearchResults, Criteria criteria)
    {
        if (tonnelSearchResults.Length < 2)
        {
            Logger.Warn($"Недостаточно результатов для second floor: {tonnelSearchResults.Length}");
            return;
        }

        var secondFloor = tonnelSearchResults[1];
        if (Equals(secondFloor.GiftId!, gift.GiftId))
            return;
        var percentDiff = (secondFloor.Price - gift.Price) / secondFloor.Price * 100.0;
        if (percentDiff > 0)
            await _telegramBot.SendSignal(gift.Name, gift.Model, gift.Price, (double)percentDiff, gift.isSold,
                gift.Activity, gift.TgUrl, gift.BotUrl, gift.SiteUrl, gift.botName, criteria,
                gift.alternativePrice, gift.lastActivity, gift.lastActivityPrice, gift.Backdrop);
    }

//     private async Task<bool> ProcessGift(TonnelRelayerHistoryGiftInfo[] historyData, string cacheKey,
//         (GiftInfo, TonnelRelayerGiftInfo) giftInfo, PortalsSearch.Result? portalsGift)
//     {
//         var lastTwoWeeksItems = historyData
//             .Where(x => x.Timestamp.HasValue && x.Timestamp.Value >= DateTimeOffset.UtcNow.AddDays(-14) &&
//                         x.GiftId > 0)
//             .ToArray();
//
//         if (lastTwoWeeksItems.Length == 0)
//         {
//             _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));
//             return true;
//         }
//
//         var activity = lastTwoWeeksItems.Length switch
//         {
//             < 5 => Activity.Low,
//             < 10 => Activity.Medium,
//             _ => Activity.High
//         };
//
//         var tonnelCurrentPrice = giftInfo.Item2.Price + giftInfo.Item2.Price * 0.1;
//         var portalsCurrentPrice = portalsGift?.Price != null
//             ? double.Parse(portalsGift.Price, NumberStyles.Any)
//             : double.MaxValue;
//         (string Name, string Model, string Backdrop, double Price, Activity Activity, string TgUrl, string botUrl,
//             string? siteUrl) gift = portalsCurrentPrice > tonnelCurrentPrice
//                 ? (giftInfo.Item2.Name!, giftInfo.Item2.Model!, giftInfo.Item2.Backdrop!, (double)tonnelCurrentPrice,
//                     activity,
//                     $"https://t.me/nft/{giftInfo.Item1.Id}",
//                     $"https://t.me/tonnel_network_bot/gift?startapp={giftInfo.Item2.GiftId}",
//                     $"https://market.tonnel.network/?giftDrawerId={giftInfo.Item2.GiftId}")
//                 : (portalsGift!.Name!, giftInfo.Item2.Model!, giftInfo.Item2.Backdrop!,
//                     portalsCurrentPrice, activity,
//                     $"https://t.me/nft/{giftInfo.Item1.Id}",
//                     $"https://t.me/portals/gift?startapp={giftInfo.Item2.GiftId}",
//                     null);
//
//         #region Peak
//
//         // максимальная цена за 14 дней
//         var maxPrice = lastTwoWeeksItems.OrderByDescending(x => x.Price).First().Price;
//         // tonnel
//         var percentDiffTonnel = (maxPrice - tonnelCurrentPrice) / maxPrice * 100.0;
//
//
//         if (percentDiffTonnel > 10)
//         {
//             _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(60));
//             Logger.Info($"""
//
//                          Подарок: {giftInfo.Item2.Name}
//                          Модель: {giftInfo.Item2.Model}
//                          Фон: {giftInfo.Item2.Backdrop}
//                          Цена сейчас: {tonnelCurrentPrice:F2}
//                          Сред. Макс. цена за 14 дней: {maxPrice:F2}
//                          Разница: {percentDiffTonnel:F2}%
//                          Активность: {activity switch
//                          {
//                              Activity.Low => "Низкая",
//                              Activity.Medium => "Средняя",
//                              _ => "Высокая"
//                          }}
//                          {(giftInfo.Item1.IsSold ? "Грязный" : null)}
//                          """);
//             // TODO
//             // await _telegramBot.SendMessageAsync(giftInfo, (double)currentPrice!, (double)middlePrice!,
//             //     (double)percentDiffTonnel, activity);
//         }
//         else
//         {
//             _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));
//         }
//
//         // portals
//         if (portalsGift?.Price == null) return false;
//         var portalsPrice = double.Parse(portalsGift.Price, NumberStyles.Any);
//         var percentDiffPortals = (maxPrice - portalsPrice) / maxPrice * 100.0;
//         if (percentDiffPortals > 10)
//         {
//             _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(60));
//             var msg = $"""
//                        Подарок: {portalsGift.Name}
//                        Модель: {giftInfo.Item2.Model}
//                        Фон: {giftInfo.Item2.Backdrop}
//                        PORTAL: {portalsGift.Price} ({percentDiffPortals:F2}%)
//                        Сред. макс. (14 дн): {maxPrice:F2}
//                        Активность: {activity switch
//                        {
//                            Activity.Low => "Низкая",
//                            Activity.Medium => "Средняя",
//                            _ => "Высокая"
//                        }}
//                        Состояние: {(giftInfo.Item1.IsSold ? "Грязный" : "Чистый")}
//                        """;
//             Logger.Info(msg);
//             // TODO
//             // await _telegramBot.SendMessage2Async(giftInfo,
//             //     portalsPrice, (double)middlePrice!,
//             //     (double)percentDiffPortals, activity, portalsGift);
//         }
//         else if (!_cache.Contains(cacheKey))
//         {
//             _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));
//         }
//
//         #endregion
//
//         return false;
//     }

    private async Task<PortalsSearch.Result?> PortalsCheckGift((GiftInfo, TonnelRelayerGiftInfo) giftInfo,
        bool searchBackdrop = true)
    {
        try
        {
            var backdrop = giftInfo.Item2.Backdrop!;
            var backdropTrimmed = backdrop[..backdrop.LastIndexOf(' ')];
            var model = giftInfo.Item2.Model!;
            var modelTrimmed = model[..model.LastIndexOf(' ')];
            var url = "https://portals-market.com/api/nfts/search?offset=0&limit=20" +
                      (searchBackdrop ? $"&filter_by_backdrops={backdropTrimmed.Replace(' ', '+')}" : string.Empty) +
                      $"&filter_by_collections={giftInfo.Item2.Name?.Replace(' ', '+')}" +
                      $"&filter_by_models={modelTrimmed.Replace(' ', '+')}" +
                      "&sort_by=price+asc" +
                      "&status=listed";
            using var response = await _portalsHttpClientPool.SendAsync(url, HttpMethod.Get);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Ошибка при получении подарка {giftInfo.Item2.GiftId} на порталах: {response.StatusCode}");
                return null;
            }

            var responseData = await response.Content.ReadFromJsonAsync<PortalsSearch>();
            if (responseData?.Results != null && responseData.Results.Length != 0)
                return responseData.Results[0];
            Logger.Warn($"Подарок {giftInfo.Item1.Id} не найден на порталах.");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Ошибка при проверке подарка {giftInfo.Item1.Id} на порталах.");
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
            var response = await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelRelayerGiftInfo[], object>(
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
                try
                {
                    var telegramGiftId = string.Concat(tonnelRelayerGiftInfo.Name?.Where(char.IsLetter) ?? string.Empty)
                                         + '-' + tonnelRelayerGiftInfo.GiftNum;
                    var giftInfo = await GiftManager.GetGiftInfoAsync(telegramGiftId);
                    await _giftInfos.Writer.WriteAsync((giftInfo, tonnelRelayerGiftInfo));
                }
                catch (Exception e)
                {
                    Logger.Warn(
                        $"Ошибка при получении информации о подарке {tonnelRelayerGiftInfo.GiftId}: {e.Message}");
                }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Ошибка при получении страницы подарков: {ex.Message}");
        }
    }
}