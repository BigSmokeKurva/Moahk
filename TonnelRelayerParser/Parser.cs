using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Caching;
using System.Threading.Channels;
using Moahk.ResponseModels;
using NLog;

namespace Moahk;

public class Parser : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MemoryCache _cache = new("TonnelRelayerParserCache");
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly Channel<(GiftInfo, TonnelRelayerGiftInfo)> _giftInfos =
        Channel.CreateBounded<(GiftInfo, TonnelRelayerGiftInfo)>(1000);

    private readonly HttpClient _portalsClient;

    private readonly TelegramBot _telegramBot = new();
    private readonly TonnelRelayerHttpClientPool _tonnelRelayerHttpClientPool = new();

    public Parser()
    {
        string[][] portalsHeaders =
        [
            ["accept", "application/json, text/plain, */*"],
            ["accept-language", "ru,en;q=0.9,en-GB;q=0.8,en-US;q=0.7"],
            ["priority", "u=1, i"],
            ["referer", "https://portals-market.com/"],
            [
                "sec-ch-ua",
                "\"Microsoft Edge\";v=\"136\", \"Microsoft Edge WebView2\";v=\"136\", \"Not.A/Brand\";v=\"99\", \"Chromium\";v=\"136\""
            ],
            ["sec-ch-ua-mobile", "?0"],
            ["sec-ch-ua-platform", "\"Windows\""],
            ["sec-fetch-dest", "empty"],
            ["sec-fetch-mode", "cors"],
            ["sec-fetch-site", "same-origin"],
            [
                "user-agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0"
            ]
        ];
        _portalsClient = new HttpClient();
        AddHeaders(_portalsClient, portalsHeaders);
    }

    public void Dispose()
    {
        _tonnelRelayerHttpClientPool.Dispose();
        _portalsClient.Dispose();
        _cancellationTokenSource.Cancel();
        GC.SuppressFinalize(this);
    }

    private static HttpClient CreateClient(WebProxy proxy, string[][] headers)
    {
        var client = new HttpClient(new HttpClientHandler { Proxy = proxy });
        AddHeaders(client, headers);
        return client;
    }

    private static void AddHeaders(HttpClient client, string[][] headers)
    {
        foreach (var h in headers)
            client.DefaultRequestHeaders.Add(h[0], h[1]);
    }

    private static WebProxy[] LoadProxies(string fileName)
    {
        var proxies = new List<WebProxy>();
        foreach (var line in File.ReadAllLines(fileName))
        {
            var parts = line.Split(':');
            if (parts.Length != 5)
            {
                Logger.Warn($"Неверный формат прокси: {fileName} {line}");
                continue;
            }

            try
            {
                var proxyUri = new Uri($"{parts[0]}://{parts[1]}:{parts[2]}");
                proxies.Add(new WebProxy(proxyUri) { Credentials = new NetworkCredential(parts[3], parts[4]) });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ошибка парсинга прокси: {fileName} {line}. {ex.Message}");
            }
        }

        if (proxies.Count == 0)
            throw new Exception($"Не удалось загрузить ни одного валидного прокси из {fileName}");
        return proxies.ToArray();
    }

    public async Task Start()
    {
        _ = RunTonnelGetMarketGiftsLoop();
        var activityThreads = new List<Task>();
        for (var i = 0; i < _tonnelRelayerHttpClientPool.Size - 1; i++)
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
            var iterationStart = DateTime.MinValue;
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

                using var response = await _tonnelRelayerHttpClientPool.PostAsJsonAsync(
                    "https://gifts2.tonnel.network/api/saleHistory",
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
                iterationStart = DateTime.UtcNow;
                if (!response.IsSuccessStatusCode)
                    throw new Exception(
                        $"Ошибка при получении истории подарков: {response.StatusCode}");
                var historyData = await response.Content.ReadFromJsonAsync<TonnelRelayerHistoryGiftInfo[]>();
                if (historyData == null)
                {
                    Logger.Warn($"Не найдено истории для подарка {giftInfo.Item1.Id}");
                    continue;
                }

                if (await ProcessGift(historyData, cacheKey, giftInfo)) continue;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ошибка при обработке подарка {giftInfo.Item1.Id}: {ex.Message}");
            }

            if (iterationStart == DateTime.MinValue) continue;
            var elapsed = DateTime.UtcNow - iterationStart;
            var delay = TimeSpan.FromSeconds(2) - elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _cancellationTokenSource.Token);
        }
    }

    private async Task<bool> ProcessGift(TonnelRelayerHistoryGiftInfo[] historyData, string cacheKey,
        (GiftInfo, TonnelRelayerGiftInfo) giftInfo)
    {
        var lastTwoWeeksItems = historyData
            .Where(x => x.Timestamp.HasValue && x.Timestamp.Value >= DateTimeOffset.UtcNow.AddDays(-14) &&
                        x.GiftId > 0)
            .ToArray();

        if (lastTwoWeeksItems.Length == 0)
        {
            _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));
            return true;
        }

        var threeMaxPriceItems = lastTwoWeeksItems.OrderByDescending(x => x.Price).Take(3).ToArray();
        var middlePrice = threeMaxPriceItems.Sum(x => x.Price) / threeMaxPriceItems.Length;
        var currentPrice = giftInfo.Item2.Price + giftInfo.Item2.Price * 0.1;
        var percentDiffTonnel = (middlePrice - currentPrice) / middlePrice * 100.0;
        var activity = lastTwoWeeksItems.Length switch
        {
            < 5 => Activity.Low,
            < 10 => Activity.Medium,
            _ => Activity.High
        };

        if (percentDiffTonnel > 10)
        {
            _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(60));
            Logger.Info($"""

                         Подарок: {giftInfo.Item2.Name}
                         Модель: {giftInfo.Item2.Model}
                         Фон: {giftInfo.Item2.Backdrop}
                         Цена сейчас: {currentPrice:F2}
                         Сред. Макс. цена за 14 дней: {middlePrice:F2}
                         Разница: {percentDiffTonnel:F2}%
                         Активность: {activity switch
                         {
                             Activity.Low => "Низкая",
                             Activity.Medium => "Средняя",
                             _ => "Высокая"
                         }}
                         {(giftInfo.Item1.IsSold ? "Грязный" : null)}
                         """);
            await _telegramBot.SendMessageAsync(giftInfo, (double)currentPrice!, (double)middlePrice!,
                (double)percentDiffTonnel, activity);
        }
        else
        {
            _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));
        }

        var portalsGift = await PortalsCheckGift(giftInfo);
        if (portalsGift?.Price == null) return false;
        var portalsPrice = double.Parse(portalsGift.Price, NumberStyles.Any);
        var percentDiffPortals = (middlePrice - portalsPrice) / middlePrice * 100.0;
        if (percentDiffPortals > 10)
        {
            _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(60));
            var msg = $"""
                       Подарок: {portalsGift.Name}
                       Модель: {giftInfo.Item2.Model}
                       Фон: {giftInfo.Item2.Backdrop}
                       PORTAL: {portalsGift.Price} ({percentDiffPortals:F2}%)
                       Сред. макс. (14 дн): {middlePrice:F2}
                       Активность: {activity switch
                       {
                           Activity.Low => "Низкая",
                           Activity.Medium => "Средняя",
                           _ => "Высокая"
                       }}
                       Состояние: {(giftInfo.Item1.IsSold ? "Грязный" : "Чистый")}
                       """;
            Logger.Info(msg);
            await _telegramBot.SendMessage2Async(giftInfo,
                portalsPrice, (double)middlePrice!,
                (double)percentDiffPortals, activity, portalsGift);
        }
        else if (!_cache.Contains(cacheKey))
        {
            _cache.Set(cacheKey, 0, DateTimeOffset.UtcNow.AddMinutes(30));
        }

        return false;
    }

    private async Task<PortalsSearch.Result?> PortalsCheckGift((GiftInfo, TonnelRelayerGiftInfo) giftInfo)
    {
        try
        {
            var backdrop = giftInfo.Item2.Backdrop!;
            var backdropTrimmed = backdrop[..backdrop.LastIndexOf(' ')];
            var model = giftInfo.Item2.Model!;
            var modelTrimmed = model[..model.LastIndexOf(' ')];
            var url = $"https://portals-market.com/api/nfts/search?offset=0&limit=20" +
                      $"&filter_by_backdrops={backdropTrimmed.Replace(' ', '+')}" +
                      $"&filter_by_collections={giftInfo.Item2.Name?.Replace(' ', '+')}" +
                      $"&filter_by_models={modelTrimmed.Replace(' ', '+')}" +
                      $"&sort_by=price+asc" +
                      $"&status=listed";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("tma", TelegramRepository.PortalsDecodedTgWebAppData);
            using var response = await _portalsClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Ошибка при получении подарка {giftInfo.Item2.GiftId} на порталах: {response.StatusCode}");
                return null;
            }

            var responseData = await response.Content.ReadFromJsonAsync<PortalsSearch>();
            if (responseData?.Results != null && responseData.Results.Length != 0)
                return responseData.Results.MinBy(x => x.Price);
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
            using var response = await _tonnelRelayerHttpClientPool.PostAsJsonAsync("https://gifts2.tonnel.network/api/pageGifts", new
            {
                page,
                limit = 30,
                sort = "{\"message_post_time\":-1,\"gift_id\":-1}",
                filter = "{\"price\":{\"$exists\":true},\"buyer\":{\"$exists\":false},\"asset\":\"TON\"}",
                @ref = 0,
                price_range = (object?)null,
                user_auth = TelegramRepository.TonnelRelayerDecodedTgWebAppData
            });
            if (!response.IsSuccessStatusCode)
                throw new Exception(
                    response.StatusCode.ToString());
            var data = await response.Content.ReadFromJsonAsync<TonnelRelayerGiftInfo[]>();
            if (data == null || data.Length == 0) throw new Exception("Не удалось получить данные о подарках.");
            foreach (var tonnelRelayerGiftInfo in data)
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