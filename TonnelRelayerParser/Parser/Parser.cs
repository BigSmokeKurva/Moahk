using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.Caching;
using System.Threading.Channels;
using Moahk.Data.Enums;
using Moahk.Other;
using Moahk.Parser.ResponseModels;
using NLog;

namespace Moahk.Parser;

public class Gift
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required string Backdrop { get; init; }
    public required double Price { get; init; }
    public required Activity Activity { get; init; }
    public required string TelegramGiftId { get; init; }
    public required string BotUrl { get; init; }
    public string? SiteUrl { get; init; }
    public required Bot Bot { get; init; }
    public GiftInfo? GiftInfo { get; set; }

    public required Bot AlternativeBot { get; init; }
    public double? AlternativePrice { get; init; }
}

public enum Bot
{
    Tonnel,
    Portals
}

public class Parser : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MemoryCache _cache = new("TonnelRelayerParserCache");
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly Channel<TonnelRelayerGiftInfo> _giftInfos =
        Channel.CreateBounded<TonnelRelayerGiftInfo>(1000);

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
            // var telegramGiftId = string.Concat(giftInfo.Name?.Where(char.IsLetter) ?? string.Empty)
            //                      + '-' + giftInfo.GiftNum;
            try
            {
                // var tgGiftInfo = await GiftManager.GetGiftInfoAsync(telegramGiftId);
                var cacheKey = (giftInfo.GiftId + giftInfo.Price).ToString() ??
                               throw new Exception("Cache key is null.");
                if (giftInfo.GiftId < 0)
                {
                    Logger.Warn($"Подарок {giftInfo.GiftId} имеет id < 0, пропускаем.");
                    continue;
                }

                if (_cache.Contains(cacheKey))
                {
                    Logger.Info($"Подарок {giftInfo.GiftId} недавно был обработан, пропускаем.");
                    continue;
                }

                _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));

                #region С фоном

                // получение activity за последние 14 дней
                var historyData = await GetTonnelActivity(giftInfo);
                if (historyData == null || historyData.Length == 0) continue;
                var lastOneWeekMaxPrice = historyData
                    .Where(x => x.Timestamp.HasValue && x.Timestamp.Value >= DateTimeOffset.UtcNow.AddDays(-7) &&
                                x.GiftId > 0)
                    .ToArray();
                var activity = lastOneWeekMaxPrice.Length switch
                {
                    < 5 => Activity.Low,
                    < 10 => Activity.Medium,
                    _ => Activity.High
                };
                // поиск самого дешевого
                //tonnel
                var tonnelGiftSearch = await TonnelSearchGift(giftInfo.Name!, giftInfo.Model!,
                    giftInfo.Backdrop);
                if (tonnelGiftSearch is null)
                {
                    Logger.Warn($"Ошибка при получении подарка {giftInfo.GiftId} на тоннеле");
                    continue;
                }

                var tonnelGiftWithMinPrice = tonnelGiftSearch.MinBy(x => x.Price);
                var tonnelPrice = CalculateTonnelPriceWithCommission((double)tonnelGiftWithMinPrice!.Price!);
                // portals
                var portalsGiftSearch = await PortalsSearchGift(giftInfo.Backdrop,
                    giftInfo.Model!, giftInfo.Name!);
                if (portalsGiftSearch?.Results is null)
                    Logger.Warn($"Ошибка при получении подарка {giftInfo.GiftId} на порталах");

                var portalsGiftWithMinPrice = portalsGiftSearch?.Results?[0];
                double? portalsPrice = portalsGiftWithMinPrice?.Price != null
                    ? double.Parse(portalsGiftWithMinPrice.Price, CultureInfo.InvariantCulture)
                    : null;
                // выбор самого дешевого подарка
                Gift gift;
                if (portalsPrice is null || portalsPrice > tonnelPrice)
                {
                    var telegramGiftId =
                        string.Concat(tonnelGiftWithMinPrice.Name?.Where(char.IsLetter) ?? string.Empty)
                        + '-' + tonnelGiftWithMinPrice.GiftNum;
                    gift = new Gift
                    {
                        Price = tonnelPrice!,
                        Activity = activity,
                        TelegramGiftId = telegramGiftId,
                        SiteUrl = $"https://market.tonnel.network/?giftDrawerId={tonnelGiftWithMinPrice.GiftId}",
                        BotUrl = $"https://t.me/tonnel_network_bot/gift?startapp={tonnelGiftWithMinPrice.GiftId}",
                        Bot = Bot.Tonnel,
                        AlternativeBot = Bot.Portals,
                        AlternativePrice = portalsPrice,
                        Name = tonnelGiftWithMinPrice.Name!,
                        Model = tonnelGiftWithMinPrice.Model!,
                        Backdrop = tonnelGiftWithMinPrice.Backdrop!
                    };
                }
                else
                {
                    var telegramGiftId =
                        string.Concat(portalsGiftWithMinPrice?.Name?.Where(char.IsLetter) ?? string.Empty)
                        + '-' + portalsGiftWithMinPrice!.ExternalCollectionNumber;
                    gift = new Gift
                    {
                        Price = (double)portalsPrice,
                        Activity = activity,
                        TelegramGiftId = telegramGiftId,
                        BotUrl = $"https://t.me/portals/market?startapp=gift_{portalsGiftWithMinPrice.Id}",
                        Bot = Bot.Portals,
                        AlternativeBot = Bot.Tonnel,
                        AlternativePrice = tonnelPrice,
                        Name = portalsGiftWithMinPrice.Name!,
                        Model = portalsGiftWithMinPrice.Attributes!.First(x => x.Type == "model").Value!,
                        Backdrop = portalsGiftWithMinPrice.Attributes!.First(x => x.Type == "backdrop").Value!
                    };
                }

                // математика
                await MathSecondFloor(gift, tonnelGiftSearch, portalsGiftSearch, lastOneWeekMaxPrice,
                    Criteria.SecondFloor);

                #endregion

                #region Без фона

                // получение activity за последние 14 дней
                historyData = await GetTonnelActivity(giftInfo, false);
                if (historyData == null || historyData.Length == 0) continue;
                lastOneWeekMaxPrice = historyData
                    .Where(x => x.Timestamp.HasValue && x.Timestamp.Value >= DateTimeOffset.UtcNow.AddDays(-7) &&
                                x.GiftId > 0)
                    .ToArray();
                activity = lastOneWeekMaxPrice.Length switch
                {
                    < 5 => Activity.Low,
                    < 10 => Activity.Medium,
                    _ => Activity.High
                };
                // поиск самого дешевого
                //tonnel
                tonnelGiftSearch = await TonnelSearchGift(giftInfo.Name!, giftInfo.Model!,
                    null);
                if (tonnelGiftSearch is null)
                {
                    Logger.Warn($"Ошибка при получении подарка {giftInfo.GiftId} на тоннеле");
                    continue;
                }

                tonnelGiftWithMinPrice = tonnelGiftSearch.MinBy(x => x.Price);
                tonnelPrice = CalculateTonnelPriceWithCommission((double)tonnelGiftWithMinPrice!.Price!);
                // portals
                portalsGiftSearch = await PortalsSearchGift(null,
                    giftInfo.Model!, giftInfo.Name!);
                if (portalsGiftSearch?.Results is null)
                    Logger.Warn($"Ошибка при получении подарка {giftInfo.GiftId} на порталах");

                portalsGiftWithMinPrice = portalsGiftSearch?.Results?[0];
                portalsPrice = portalsGiftWithMinPrice?.Price != null
                    ? double.Parse(portalsGiftWithMinPrice.Price, CultureInfo.InvariantCulture)
                    : null;
                // выбор самого дешевого подарка
                if (portalsPrice is null || portalsPrice > tonnelPrice)
                {
                    var telegramGiftId =
                        string.Concat(tonnelGiftWithMinPrice.Name?.Where(char.IsLetter) ?? string.Empty)
                        + '-' + tonnelGiftWithMinPrice.GiftNum;
                    gift = new Gift
                    {
                        Price = tonnelPrice!,
                        Activity = activity,
                        TelegramGiftId = telegramGiftId,
                        SiteUrl = $"https://market.tonnel.network/?giftDrawerId={tonnelGiftWithMinPrice.GiftId}",
                        BotUrl = $"https://t.me/tonnel_network_bot/gift?startapp={tonnelGiftWithMinPrice.GiftId}",
                        Bot = Bot.Tonnel,
                        AlternativeBot = Bot.Portals,
                        AlternativePrice = portalsPrice,
                        Name = tonnelGiftWithMinPrice.Name!,
                        Model = tonnelGiftWithMinPrice.Model!,
                        Backdrop = tonnelGiftWithMinPrice.Backdrop!
                    };
                }
                else
                {
                    var telegramGiftId =
                        string.Concat(portalsGiftWithMinPrice?.Name?.Where(char.IsLetter) ?? string.Empty)
                        + '-' + portalsGiftWithMinPrice!.ExternalCollectionNumber;
                    gift = new Gift
                    {
                        Price = (double)portalsPrice,
                        Activity = activity,
                        TelegramGiftId = telegramGiftId,
                        BotUrl = $"https://t.me/portals/market?startapp=gift_{portalsGiftWithMinPrice.Id}",
                        Bot = Bot.Portals,
                        AlternativeBot = Bot.Tonnel,
                        AlternativePrice = tonnelPrice,
                        Name = portalsGiftWithMinPrice.Name!,
                        Model = portalsGiftWithMinPrice.Attributes!.First(x => x.Type == "model").Value!,
                        Backdrop = portalsGiftWithMinPrice.Attributes!.First(x => x.Type == "backdrop").Value!
                    };
                }

                // математика
                await MathSecondFloor(gift, tonnelGiftSearch, portalsGiftSearch, lastOneWeekMaxPrice,
                    Criteria.SecondFloorWithoutBackdrop);

                #endregion
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Ошибка при обработке подарка {giftInfo.GiftId}");
            }
        }
    }

    private async Task<TonnelRelayerHistoryGiftInfo[]?> GetTonnelActivity(TonnelRelayerGiftInfo giftInfo,
        bool searchBackdrop = true)
    {
        TonnelRelayerHistoryGiftInfo[]? response;
        if (searchBackdrop)
            response = await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelRelayerHistoryGiftInfo[], object>(
                "https://gifts3.tonnel.network/api/saleHistory",
                new
                {
                    authData = TelegramRepository.TonnelRelayerDecodedTgWebAppData,
                    page = 1,
                    limit = 50,
                    type = "ALL",
                    filter = new
                    {
                        gift_name = giftInfo.Name,
                        model = giftInfo.Model,
                        backdrop = giftInfo.Backdrop
                    },
                    sort = new { timestamp = -1, gift_id = -1 }
                });
        else
            response = await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelRelayerHistoryGiftInfo[], object>(
                "https://gifts3.tonnel.network/api/saleHistory",
                new
                {
                    authData = TelegramRepository.TonnelRelayerDecodedTgWebAppData,
                    page = 1,
                    limit = 50,
                    type = "ALL",
                    filter = new
                    {
                        gift_name = giftInfo.Name,
                        model = giftInfo.Model
                    },
                    sort = new { timestamp = -1, gift_id = -1 }
                });

        return response;
    }

    private async Task<TonnelSearch[]?> TonnelSearchGift(string name, string model, string? backdrop)
    {
        try
        {
            var response = await _tonnelRelayerBrowserContextPool.PostAsJsonAsync<TonnelSearch[], object>(
                "https://gifts3.tonnel.network/api/pageGifts", new
                {
                    page = 1,
                    limit = 30,
                    sort = "{\"price\":1,\"gift_id\":-1}",
                    filter =
                        "{\"price\":{\"$exists\":true},\"buyer\":{\"$exists\":false},\"gift_name\":\"" +
                        name + "\",\"model\":\"" + model + "\"," + (backdrop is not null
                            ? "\"backdrop\":{\"$in\":[\"" +
                              backdrop + "\"]},"
                            : string.Empty) + "\"asset\":\"TON\"}",
                    @ref = 0,
                    price_range = (object?)null,
                    user_auth =
                        TelegramRepository.TonnelRelayerDecodedTgWebAppData
                });
            if (response is null || response.Length == 0) return null;
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
        return price + price * 0.04;
    }

    private async Task MathSecondFloor(
        Gift gift,
        TonnelSearch[] tonnelSearch, PortalsSearch? portalsSearch, TonnelRelayerHistoryGiftInfo[] lastOneWeekMaxPrice,
        Criteria criteria)
    {
        double secondFloorPrice;
        switch (portalsSearch?.Results)
        {
            case null or { Length: < 2 } when tonnelSearch.Length >= 2:
                secondFloorPrice = CalculateTonnelPriceWithCommission((double)tonnelSearch[1].Price!);
                break;
            case { Length: >= 2 } when tonnelSearch.Length < 2:
            {
                var portalsSecondFloorPrice =
                    double.Parse(portalsSearch.Results?[1].Price!, CultureInfo.InvariantCulture);
                secondFloorPrice = portalsSecondFloorPrice;
                break;
            }
            case { Length: >= 2 } when gift.Bot == Bot.Tonnel:
            {
                var portalsSecondFloorPrice =
                    double.Parse(portalsSearch.Results?[0].Price!, CultureInfo.InvariantCulture);
                var tonnelSecondFloorPrice = CalculateTonnelPriceWithCommission((double)tonnelSearch[1].Price!);
                secondFloorPrice = tonnelSecondFloorPrice > portalsSecondFloorPrice
                    ? portalsSecondFloorPrice
                    : tonnelSecondFloorPrice;

                break;
            }
            // portals
            case { Length: >= 2 }:
            {
                var portalsSecondFloorPrice =
                    double.Parse(portalsSearch.Results?[1].Price!, CultureInfo.InvariantCulture);
                var tonnelSecondFloorPrice = CalculateTonnelPriceWithCommission((double)tonnelSearch[0].Price!);
                secondFloorPrice = tonnelSecondFloorPrice > portalsSecondFloorPrice
                    ? portalsSecondFloorPrice
                    : tonnelSecondFloorPrice;

                break;
            }
            default:
            {
                if (tonnelSearch.Length >= 2)
                {
                    if (gift.Bot == Bot.Tonnel)
                    {
                        var portalsSecondFloorPrice =
                            double.Parse(portalsSearch?.Results?[0].Price!, CultureInfo.InvariantCulture);
                        var tonnelSecondFloorPrice = CalculateTonnelPriceWithCommission((double)tonnelSearch[1].Price!);
                        secondFloorPrice = portalsSecondFloorPrice > tonnelSecondFloorPrice
                            ? tonnelSecondFloorPrice
                            : portalsSecondFloorPrice;
                    }
                    // portals
                    else
                    {
                        var portalsSecondFloorPrice =
                            double.Parse(portalsSearch?.Results?[1].Price!, CultureInfo.InvariantCulture);
                        var tonnelSecondFloorPrice = CalculateTonnelPriceWithCommission((double)tonnelSearch[0].Price!);
                        secondFloorPrice = portalsSecondFloorPrice > tonnelSecondFloorPrice
                            ? tonnelSecondFloorPrice
                            : portalsSecondFloorPrice;
                    }
                }
                else
                {
                    Logger.Warn("Недостаточно результатов для second floor");
                    return;
                }

                break;
            }
        }

        var percentDiff = (secondFloorPrice - gift.Price) / secondFloorPrice * 100.0;
        if (percentDiff < 0)
            return;
        var lastTwoWeeksPrices = lastOneWeekMaxPrice.Select(x => (double)x.Price!).ToArray();
        double? percentile25 = null;
        try
        {
            percentile25 = lastTwoWeeksPrices.Percentile(25);
        }
        catch
        {
            // ignored
        }

        double? percentile75 = null;
        try
        {
            percentile75 = lastTwoWeeksPrices.Percentile(75);
        }
        catch
        {
            // ignored
        }

        var lastTwoWeeksMaxPrice = lastOneWeekMaxPrice.OrderByDescending(x => x.Price).FirstOrDefault()?.Price;
        if (gift.GiftInfo is null)
        {
            var telegramGiftInfo = await GiftManager.GetGiftInfoAsync(gift.TelegramGiftId);
            gift.GiftInfo = telegramGiftInfo;
        }

        await _telegramBot.SendSignal(gift, percentDiff, secondFloorPrice, percentile25, percentile75,
            lastTwoWeeksMaxPrice, criteria);
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

    private async Task<PortalsSearch?> PortalsSearchGift(string? backdrop, string model, string collection)
    {
        try
        {
            var url = "https://portals-market.com/api/nfts/search?offset=0&limit=20" +
                      (backdrop is not null
                          ? $"&filter_by_backdrops={backdrop[..backdrop.LastIndexOf(' ')].Replace(' ', '+')}"
                          : string.Empty) +
                      $"&filter_by_collections={collection.Replace(' ', '+')}" +
                      $"&filter_by_models={model[..model.LastIndexOf(' ')].Replace(' ', '+')}" +
                      "&sort_by=price+asc" +
                      "&status=listed";
            using var response = await _portalsHttpClientPool.SendAsync(url, HttpMethod.Get);
            if (!response.IsSuccessStatusCode) return null;

            var responseData = await response.Content.ReadFromJsonAsync<PortalsSearch>();
            if (responseData?.Results != null && responseData.Results.Length != 0)
                return responseData;
            return null;
        }
        catch (Exception)
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
                    await _giftInfos.Writer.WriteAsync(tonnelRelayerGiftInfo);
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