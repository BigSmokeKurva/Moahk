using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Caching;
using NLog;

namespace Moahk.Parser;

public class PortalsHttpClientPool : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MemoryCache _cache = new("PortalsHttpClientPool");
    private readonly (HttpClient Client, string Id)[] _httpClients;

    public PortalsHttpClientPool()
    {
        string[][] headers =
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
        var proxies = LoadProxies();
        if (proxies.Length == 0)
            throw new Exception("Нет доступных прокси");
        _httpClients = proxies.Select((proxy, index) =>
        {
            var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true,
                UseCookies = false
            };
            var client = new HttpClient(handler);
            foreach (var header in headers) client.DefaultRequestHeaders.Add(header[0], header[1]);
            client.DefaultRequestHeaders.ConnectionClose = true;

            return (client, index.ToString());
        }).ToArray();
        Logger.Info($"Создан пул Portals HTTP клиентов с {proxies.Length} прокси");
    }

    public void Dispose()
    {
        foreach (var httpClient in _httpClients) httpClient.Client.Dispose();

        GC.SuppressFinalize(this);
    }

    private static WebProxy[] LoadProxies()
    {
        const string fileName = "proxies.txt";
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

    private async Task<HttpClient> GetHttpClient()
    {
        while (true)
        {
            HttpClient? client = null;
            lock (this)
            {
                var clientTuple = _httpClients.FirstOrDefault(c => !_cache.Contains(c.Id));
                if (clientTuple.Client != null)
                {
                    client = clientTuple.Client;
                    _cache.Add(clientTuple.Id, 0, DateTimeOffset.UtcNow.AddSeconds(2));
                }
            }

            if (client != null) return client;
            await Task.Delay(100);
        }
    }

    public async Task<HttpResponseMessage> SendAsync(string url, HttpMethod method)
    {
        for (var i = 0; i < 3; i++)
            try
            {
                var client = await GetHttpClient();
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("tma", TelegramAccountRepository.PortalsDecodedTgWebAppData);
                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn(
                        $"Попытка {i + 1}: Ошибка при запросе к {url}. Код ответа: {(int)response.StatusCode}");
                    continue;
                }

                return response;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Попытка {i + 1}: Ошибка при запросе к {url}");
            }

        throw new Exception($"Не удалось выполнить запрос к {url} после 3 попыток");
    }
}