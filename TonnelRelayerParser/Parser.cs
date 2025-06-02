using System.Net;
using System.Net.Http.Json;
using System.Runtime.Caching;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using NLog;

namespace TonnelRelayerParser;

public class Parser : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly WebProxy[] PageProxies = LoadProxies("page_proxies.txt");
    private static readonly WebProxy[] ActivityProxies = LoadProxies("activity_proxies.txt");
    private readonly HttpClient[] _activityClients;
    private readonly MemoryCache _cache = new("TonnelRelayerParserCache");
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly Channel<(GiftInfo, TonnelRelayerGiftInfo)> _giftInfos =
        Channel.CreateBounded<(GiftInfo, TonnelRelayerGiftInfo)>(1000);

    private readonly HttpClient[] _pageClients;
    private readonly TelegramBot _telegramBot = new();

    static Parser()
    {
        Logger.Info($"Загружено {PageProxies.Length} page-прокси и {ActivityProxies.Length} activity-прокси.");
    }

    public Parser()
    {
        var headers = new[]
        {
            new[] { "accept", "*/*" },
            new[] { "accept-language", "ru,en;q=0.9,en-GB;q=0.8,en-US;q=0.7" },
            new[] { "origin", "https://marketplace.tonnel.network" },
            new[] { "priority", "u=1, i" },
            new[] { "referer", "https://marketplace.tonnel.network/" },
            new[]
            {
                "sec-ch-ua",
                "\"Microsoft Edge\";v=\"136\", \"Microsoft Edge WebView2\";v=\"136\", \"Not.A/Brand\";v=\"99\", \"Chromium\";v=\"136\""
            },
            new[] { "sec-ch-ua-mobile", "?0" },
            new[] { "sec-ch-ua-platform", "\"Windows\"" },
            new[] { "sec-fetch-dest", "empty" },
            new[] { "sec-fetch-mode", "cors" },
            new[] { "sec-fetch-site", "same-site" },
            new[]
            {
                "user-agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0"
            }
        };
        _pageClients = PageProxies.Select(x => CreateClient(x, headers)).ToArray();
        _activityClients = ActivityProxies.Select(x => CreateClient(x, headers)).ToArray();
    }

    public void Dispose()
    {
        foreach (var client in _pageClients.Concat(_activityClients)) client.Dispose();
        _cancellationTokenSource.Cancel();
        GC.SuppressFinalize(this);
    }

    private static HttpClient CreateClient(WebProxy proxy, string[][] headers)
    {
        var client = new HttpClient(new HttpClientHandler { Proxy = proxy });
        foreach (var h in headers)
            client.DefaultRequestHeaders.Add(h[0], h[1]);
        return client;
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
        _ = RunGetMarketGiftsLoop();
        _ = Task.WhenAll(_activityClients.Select(ActivityThread));
    }

    private async Task ActivityThread(HttpClient client)
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            var iterationStarted = false;
            var iterationStart = DateTime.MinValue;
            var giftInfo = await _giftInfos.Reader.ReadAsync();
            try
            {
                if (giftInfo.Item2.GiftId < 0)
                {
                    Logger.Warn($"Подарок {giftInfo.Item1.Id} имеет id < 0, пропускаем.");
                    continue;
                }

                if (_cache.Contains(giftInfo.Item1.Id + giftInfo.Item2.Price))
                {
                    Logger.Info($"Подарок {giftInfo.Item1.Id} недавно был обработан, пропускаем.");
                    continue;
                }

                using var response = await client.PostAsJsonAsync(
                    "https://gifts2.tonnel.network/api/saleHistory",
                    new
                    {
                        authData = TelegramRepository.TonnelRelayerTgWebAppData,
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
                iterationStarted = true;
                iterationStart = DateTime.UtcNow;
                if (!response.IsSuccessStatusCode)
                    throw new Exception(
                        $"Ошибка при получении истории подарков: {response.StatusCode} {response.ReasonPhrase}");
                var historyData = await response.Content.ReadFromJsonAsync<TonnelRelayerHistoryGiftInfo[]>();
                if (historyData == null)
                {
                    Logger.Warn($"Не найдено истории для подарка {giftInfo.Item1.Id}");
                    continue;
                }

                var lastTwoWeeksItems = historyData
                    .Where(x => x.Timestamp.HasValue && x.Timestamp.Value >= DateTimeOffset.UtcNow.AddDays(-14) &&
                                x.GiftId > 0);
                var relayerHistoryGiftInfos =
                    lastTwoWeeksItems as TonnelRelayerHistoryGiftInfo[] ?? lastTwoWeeksItems.ToArray();
                var tonnelRelayerHistoryGiftInfos =
                    lastTwoWeeksItems as TonnelRelayerHistoryGiftInfo[] ?? relayerHistoryGiftInfos.ToArray();
                if (tonnelRelayerHistoryGiftInfos.Length != 0)
                {
                    var threeMaxPriceItems = tonnelRelayerHistoryGiftInfos
                        .OrderByDescending(x => x.Price).Take(3).ToArray();
                    var middlePrice = threeMaxPriceItems.Sum(x => x.Price) / threeMaxPriceItems.Length;
                    var currentPrice = giftInfo.Item2.Price + giftInfo.Item2.Price * 0.1;
                    var percentDiff = (middlePrice - currentPrice) / middlePrice * 100.0;
                    if (percentDiff > 10)
                    {
                        var activity = relayerHistoryGiftInfos.Length switch
                        {
                            < 5 => Activity.Low,
                            < 10 => Activity.Medium,
                            _ => Activity.High
                        };
                        _cache.Set(giftInfo.Item1.Id + giftInfo.Item2.Price, 0,
                            DateTimeOffset.UtcNow.AddMinutes(60));
                        Logger.Info($"""

                                     Подарок: {giftInfo.Item2.Name}
                                     Модель: {giftInfo.Item2.Model}
                                     Фон: {giftInfo.Item2.Backdrop}
                                     Цена сейчас: {currentPrice:F2}
                                     Сред. Макс. цена за 14 дней: {middlePrice:F2}
                                     Разница: {percentDiff:F2}%
                                     Активность: {activity switch
                                     {
                                         Activity.Low => "Низкая",
                                         Activity.Medium => "Средняя",
                                         _ => "Высокая"
                                     }}
                                     {(giftInfo.Item1.IsSold ? "Грязный" : null)}
                                     """);
                        await _telegramBot.SendMessageAsync(giftInfo, (double)currentPrice!, (double)middlePrice!,
                            (double)percentDiff, activity);
                    }
                    else
                    {
                        _cache.Set(giftInfo.Item1.Id + giftInfo.Item2.Price, 0,
                            DateTimeOffset.UtcNow.AddMinutes(30));
                    }
                }
                else
                {
                    _cache.Set(giftInfo.Item1.Id + giftInfo.Item2.Price, 0,
                        DateTimeOffset.UtcNow.AddMinutes(30));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ошибка при обработке подарка {giftInfo.Item1.Id}: {ex.Message}");
            }

            if (!iterationStarted) continue;
            var elapsed = DateTime.UtcNow - iterationStart;
            var delay = TimeSpan.FromSeconds(2) - elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _cancellationTokenSource.Token);
        }
    }

    private async Task RunGetMarketGiftsLoop()
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
        await Task.WhenAll(_pageClients.Select((client, idx) => GetMarketGiftPage(client, idx + 1)));
    }

    private async Task GetMarketGiftPage(HttpClient client, int page)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("https://gifts2.tonnel.network/api/pageGifts", new
            {
                page,
                limit = 30,
                sort = "{\"message_post_time\":-1,\"gift_id\":-1}",
                filter = "{\"price\":{\"$exists\":true},\"buyer\":{\"$exists\":false},\"asset\":\"TON\"}",
                @ref = 0,
                price_range = (object?)null,
                user_auth = TelegramRepository.TonnelRelayerTgWebAppData
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

public class TonnelRelayerGiftInfo
{
    [JsonPropertyName("gift_num")] public long? GiftNum { get; set; }
    [JsonPropertyName("customEmojiId")] public string? CustomEmojiId { get; set; }
    [JsonPropertyName("gift_id")] public long GiftId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("asset")] public string? Asset { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("backdrop")] public string? Backdrop { get; set; }

    [JsonPropertyName("availabilityIssued")]
    public object? AvailabilityIssued { get; set; }

    [JsonPropertyName("availabilityTotal")]
    public object? AvailabilityTotal { get; set; }

    [JsonPropertyName("message_in_channel")]
    public object? MessageInChannel { get; set; }

    [JsonPropertyName("price")] public double? Price { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("limited")] public bool? Limited { get; set; }
    [JsonPropertyName("auction")] public object? Auction { get; set; }
    [JsonPropertyName("export_at")] public DateTimeOffset? ExportAt { get; set; }
    [JsonPropertyName("premarket")] public bool? Premarket { get; set; }
    [JsonPropertyName("premarketStage")] public long? PremarketStage { get; set; }
}

public class TonnelRelayerHistoryGiftInfo
{
    [JsonPropertyName("_id")] public string? Id { get; set; }
    [JsonPropertyName("gift_id")] public long? GiftId { get; set; }
    [JsonPropertyName("gift_num")] public long? GiftNum { get; set; }
    [JsonPropertyName("gift_name")] public string? GiftName { get; set; }
    [JsonPropertyName("bidder")] public long? Bidder { get; set; }
    [JsonPropertyName("price")] public double? Price { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("backdrop")] public string? Backdrop { get; set; }
    [JsonPropertyName("asset")] public string? Asset { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("__v")] public long? V { get; set; }
}

public enum Activity
{
    Low,
    Medium,
    High
}